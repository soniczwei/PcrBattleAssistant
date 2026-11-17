using System.IO;
using System.Text.Json;

namespace PcrBattleAssistant.Services;

/// <summary>
/// 模块 D：WebView2 页面净化与 React 状态静默模拟录入桥接服务
/// </summary>
public static class WebViewBridgeService
{
    public const string BattlePageUrl = "https://www.pcrdfans.com/battle";
    public const string UploadPageUrl = "https://www.pcrdfans.com/battle/upload";

    /// <summary>
    /// 通过 CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync 在文档创建阶段注入的预加载脚本：
    /// 保留原网站完整页面与广告展示，仅提供隐藏原生选人面板的辅助样式。
    /// </summary>
    public static string GetDocumentCreatedPurifyScript() => """
        (function () {
            const STYLE_ID = 'pcr-assistant-purify-style';
            const cssText = `
                /* 当开启“隐藏选人面板”时，将 #battle_chara_select 移出可视区（不使用 display: none） */
                body.pcr-hide-native-picker #battle_chara_select {
                    position: absolute !important;
                    left: -9999px !important;
                    width: 600px !important;
                    height: 0 !important;
                    overflow: hidden !important;
                    pointer-events: auto !important;
                    opacity: 0.01 !important;
                }
            `;

            function ensurePurifyStyle() {
                if (!document.documentElement) return;
                if (!document.getElementById(STYLE_ID)) {
                    const style = document.createElement('style');
                    style.id = STYLE_ID;
                    style.textContent = cssText;
                    (document.head || document.documentElement).appendChild(style);
                }
            }

            ensurePurifyStyle();
            window.addEventListener('DOMContentLoaded', ensurePurifyStyle);
        })();
        """;

    /// <summary>
    /// 仅切换网页端 1队 / 2队 / 3队 标签页，不重新触发搜索请求
    /// </summary>
    public static string BuildSwitchTeamTabOnlyScript(int teamNumber)
    {
        string teamLabel = $"{Math.Clamp(teamNumber, 1, 3)}队";
        return $$"""
            (function () {
                const targetTeamLabel = '{{teamLabel}}';
                const tabs = Array.from(document.querySelectorAll('.ant-tabs-tab, [role="tab"]'));
                for (const tab of tabs) {
                    if ((tab.textContent || '').trim() === targetTeamLabel) {
                        if (!tab.classList.contains('ant-tabs-tab-active')) {
                            if (typeof tab.click === 'function') {
                                tab.click();
                            } else {
                                tab.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
                            }
                        }
                        return true;
                    }
                }
                return false;
            })();
            """;
    }

    /// <summary>
    /// 设置“信任作业置顶”开关（点赞过 liked || 自己上传 iseditor 排在当前页最前），并安装 DOM 变化监听以支持翻页/重新搜索自动生效
    /// </summary>
    public static string BuildSetPrioritizeTrustedScript(bool enabled)
    {
        string enabledLiteral = enabled ? "true" : "false";
        return $$"""
            (function () {
                window.__pcrPrioritizeTrusted = {{enabledLiteral}};

                function findBattlePageInstance() {
                    const candidates = [
                        document.querySelector('.body_div_ctn'),
                        document.getElementById('battle_chara_select'),
                        document.getElementById('__next')
                    ];
                    for (const el of candidates) {
                        if (!el) continue;
                        for (const key of Object.keys(el)) {
                            if (key.startsWith('__reactInternalInstance$') || key.startsWith('__reactFiber$')) {
                                let fiber = el[key];
                                for (let depth = 0; depth < 15 && fiber; depth++) {
                                    const inst = fiber.stateNode;
                                    if (inst && inst.state && ('data1' in inst.state) && ('data2' in inst.state) && ('data3' in inst.state)) {
                                        return inst;
                                    }
                                    fiber = fiber.return;
                                }
                            }
                        }
                    }
                    return null;
                }

                function isTrustedItem(item) {
                    return !!(item && !item.disliked && (item.liked || item.iseditor));
                }

                function needsTrustedSort(arr) {
                    let seenUntrusted = false;
                    for (const item of arr) {
                        const trusted = isTrustedItem(item);
                        if (trusted && seenUntrusted) return true;
                        if (!trusted) seenUntrusted = true;
                    }
                    return false;
                }

                function applyTrustedSort() {
                    if (!window.__pcrPrioritizeTrusted) return;
                    const inst = findBattlePageInstance();
                    if (!inst || !inst.state) return;

                    const patch = {};
                    let changed = false;
                    for (const dataKey of ['data1', 'data2', 'data3']) {
                        const d = inst.state[dataKey];
                        if (d && Array.isArray(d.result) && d.result.length > 1 && needsTrustedSort(d.result)) {
                            const trusted = [];
                            const others = [];
                            for (const item of d.result) {
                                if (isTrustedItem(item)) {
                                    trusted.push(item);
                                } else {
                                    others.push(item);
                                }
                            }
                            patch[dataKey] = Object.assign({}, d, { result: trusted.concat(others) });
                            changed = true;
                        }
                    }

                    if (changed) {
                        inst.setState(patch);
                    }
                }

                window.__pcrApplyTrustedSort = applyTrustedSort;

                if (!window.__pcrTrustedSortObserverInstalled) {
                    window.__pcrTrustedSortObserverInstalled = true;
                    let sortTimer = null;
                    const observer = new MutationObserver(() => {
                        if (!window.__pcrPrioritizeTrusted) return;
                        if (sortTimer) clearTimeout(sortTimer);
                        sortTimer = setTimeout(() => {
                            if (typeof window.__pcrApplyTrustedSort === 'function') {
                                window.__pcrApplyTrustedSort();
                            }
                        }, 120);
                    });
                    const root = document.getElementById('__next') || document.body;
                    if (root) {
                        observer.observe(root, { childList: true, subtree: true });
                    }
                }

                if (window.__pcrPrioritizeTrusted) {
                    applyTrustedSort();
                }

                return window.__pcrPrioritizeTrusted;
            })();
            """;
    }

    /// <summary>
    /// 从 /battle 页面 #battle_chara_select 提取全部角色的 3★ 雪碧图坐标 (cid -> {x, y})，
    /// 并从 __NEXT_DATA__ 提取 maxrarity === 6 的角色列表计算 6★ 雪碧图坐标，随后闭合面板
    /// </summary>
    public static string GetExtractSpriteMapScript() => $$"""
        (async function () {
            {{SharedDomHelpersJs}}
            let pickerRoot = document.getElementById('battle_chara_select');
            for (let wait = 0; wait < 15 && !pickerRoot; wait++) {
                await sleep(200);
                pickerRoot = document.getElementById('battle_chara_select');
            }
            if (!pickerRoot) return '';

            await switchToAllAttributes(document);
            await expandCharaPanels(pickerRoot);

            let divs = [];
            for (let retry = 0; retry < 15; retry++) {
                divs = Array.from(pickerRoot.querySelectorAll('div')).filter(d => {
                    const bg = (d.style && d.style.backgroundImage) || window.getComputedStyle(d).backgroundImage || '';
                    return bg.includes('charas');
                });
                if (divs.length >= 100) break;
                await sleep(150);
            }

            const star3 = {};
            for (const d of divs) {
                const cid = getUnitIdFromReactNode(d);
                if (cid < 100000) continue;
                const cs = window.getComputedStyle(d);
                const w = parseFloat(d.style.width) || parseFloat(cs.width) || 50;
                const scale = w / 60.0;
                const posX = parseFloat(d.style.backgroundPositionX || cs.backgroundPositionX) || 0;
                const posY = parseFloat(d.style.backgroundPositionY || cs.backgroundPositionY) || 0;
                const x = Math.round(-posX / scale);
                const y = Math.round(-posY / scale);
                if (x >= 0 && y >= 0) {
                    star3[String(cid)] = { x: x, y: y };
                }
            }

            await collapseCharaPanels(pickerRoot);

            const star6 = {};
            try {
                const unitObj = window.__NEXT_DATA__?.props?.initialState?.server?.unitObject;
                if (unitObj) {
                    const star6Ids = Object.keys(unitObj)
                        .filter(k => unitObj[k] && Number(unitObj[k].maxrarity) === 6)
                        .map(Number)
                        .filter(id => id >= 100000)
                        .sort((a, b) => a - b);

                    for (let idx = 0; idx < star6Ids.length; idx++) {
                        const L = Math.floor(Math.sqrt(idx));
                        const rem = idx - L * L;
                        const col = rem < L ? L : (rem - L);
                        const row = rem < L ? rem : L;
                        star6[String(star6Ids[idx])] = { x: col * 62, y: row * 62 };
                    }
                }
            } catch (e) {
            }

            return JSON.stringify({ star3: star3, star6: star6 });
        })();
        """;

    private const string SharedDomHelpersJs = """
        const sleep = (ms) => new Promise(r => setTimeout(r, ms));

        function nativeClick(el) {
            if (!el) return false;
            if (typeof el.click === 'function') {
                el.click();
                return true;
            }
            el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window }));
            return true;
        }

        function getUnitIdFromReactNode(domNode) {
            if (!domNode) return 0;
            const keys = Object.keys(domNode);
            for (const key of keys) {
                if (key.startsWith('__reactInternalInstance$') || key.startsWith('__reactFiber$')) {
                    let fiber = domNode[key];
                    for (let depth = 0; depth < 7 && fiber; depth++) {
                        const props = fiber.memoizedProps;
                        if (props && typeof props.cid === 'number' && props.cid >= 100000) {
                            return props.cid;
                        }
                        fiber = fiber.return;
                    }
                }
            }
            return 0;
        }

        async function switchToAllAttributes(scopeRoot) {
            const root = scopeRoot || document;
            const filterGroups = Array.from(root.querySelectorAll('.battle_attribute_filter'));
            const groups = filterGroups.length > 0 ? filterGroups : Array.from(document.querySelectorAll('.battle_attribute_filter'));
            let switched = false;
            for (const group of groups) {
                const buttons = Array.from(group.querySelectorAll('label.ant-radio-button-wrapper'));
                const allBtn = buttons.find(b => {
                    const txt = (b.textContent || '').replace(/\s+/g, '');
                    return txt.includes('全属性') || txt.includes('全部') || txt === 'All';
                }) || buttons[0];
                if (allBtn && !allBtn.classList.contains('ant-radio-button-wrapper-checked')) {
                    nativeClick(allBtn);
                    switched = true;
                }
            }
            if (switched) {
                await sleep(60);
            }
        }

        async function expandCharaPanels(scopeRoot) {
            const root = scopeRoot || document.getElementById('battle_chara_select') || document;
            const collapsedHeaders = Array.from(root.querySelectorAll('.ant-collapse-header[aria-expanded="false"], [aria-expanded="false"]'));
            let count = 0;
            for (const header of collapsedHeaders) {
                nativeClick(header);
                count++;
            }
            if (count > 0) {
                await sleep(180);
            }
        }

        async function collapseCharaPanels(scopeRoot) {
            const root = scopeRoot || document.getElementById('battle_chara_select') || document;
            const expandedHeaders = Array.from(root.querySelectorAll('.ant-collapse-header[aria-expanded="true"], [aria-expanded="true"]'));
            let count = 0;
            for (const header of expandedHeaders) {
                nativeClick(header);
                count++;
            }
            if (count > 0) {
                await sleep(60);
            }
        }

        function findAvatarDiv(allAvatarDivs, t) {
            let matchedDiv = allAvatarDivs.find(d => getUnitIdFromReactNode(d) === t.unitId);
            if (!matchedDiv && t.spriteX >= 0 && t.spriteY >= 0) {
                matchedDiv = allAvatarDivs.find(d => {
                    const w = parseFloat(d.style.width) || 50;
                    const scale = w / 60.0;
                    const expectedX = -(t.spriteX * scale);
                    const expectedY = -(t.spriteY * scale);
                    const actualX = parseFloat(d.style.backgroundPositionX) || 0;
                    const actualY = parseFloat(d.style.backgroundPositionY) || 0;
                    return Math.abs(actualX - expectedX) < 1.2 && Math.abs(actualY - expectedY) < 1.2;
                });
            }
            return matchedDiv;
        }
        """;

    /// <summary>
    /// 单队查询：向指定队伍标签页（默认 1队）填入 5 名角色并触发搜索
    /// </summary>
    public static string BuildAutoSelectAndQueryScript(
        IReadOnlyList<int> unitIds,
        int teamNumber = 1,
        bool triggerSearch = true)
    {
        var spriteCoords = LoadSpriteCoordLookup();

        var targetItems = unitIds
            .Where(id => id > 0)
            .Select(id =>
            {
                int base6 = id >= 100000 ? (id / 100) * 100 + 1 : id * 100 + 1;
                (int X, int Y) coord = spriteCoords.GetValueOrDefault(base6, (-1, -1));
                return new
                {
                    unitId = base6,
                    spriteX = coord.X,
                    spriteY = coord.Y
                };
            })
            .ToList();

        string targetsJson = JsonSerializer.Serialize(targetItems);
        string teamLabel = $"{Math.Clamp(teamNumber, 1, 3)}队";
        string autoSearchLiteral = triggerSearch ? "true" : "false";

        return $$"""
            (async function () {
                {{SharedDomHelpersJs}}
                const targets = {{targetsJson}};
                const targetTeamLabel = '{{teamLabel}}';
                const shouldSearch = {{autoSearchLiteral}};

                // 切换目标队伍标签页 (1队 / 2队 / 3队)
                const tabs = Array.from(document.querySelectorAll('.ant-tabs-tab, [role="tab"]'));
                for (const tab of tabs) {
                    if ((tab.textContent || '').trim() === targetTeamLabel) {
                        if (!tab.classList.contains('ant-tabs-tab-active')) {
                            nativeClick(tab);
                            await sleep(80);
                        }
                        break;
                    }
                }

                // 1. 切换到“全属性”，避免用户此前筛选属性导致漏选角色
                await switchToAllAttributes(document);

                // 2. 展开“前卫 / 中卫 / 后卫”折叠面板（首次挂载需要展开 DOM）
                const pickerRoot = document.getElementById('battle_chara_select') || document.body;
                await expandCharaPanels(pickerRoot);

                // 3. 清空当前队伍已选的旧角色
                const searchSelectBoxes = Array.from(document.querySelectorAll('.battle_search_select:not(.battle_search_result_ctn)'));
                if (searchSelectBoxes.length > 0) {
                    const activeSlotBox = searchSelectBoxes[0];
                    const selectedDivs = Array.from(activeSlotBox.querySelectorAll('div')).filter(d => {
                        return getUnitIdFromReactNode(d) > 0 || (d.style && d.style.backgroundImage && d.style.backgroundImage.includes('charas'));
                    });
                    for (const selDiv of selectedDivs) {
                        nativeClick(selDiv);
                        await sleep(30);
                    }
                }

                // 4. 在 #battle_chara_select 选人区查找目标 UnitID 的角色节点并点击
                const allAvatarDivs = Array.from(pickerRoot.querySelectorAll('div')).filter(d => {
                    return d.style && d.style.backgroundImage && d.style.backgroundImage.includes('charas');
                });

                const clickedUnits = [];
                const missingUnits = [];

                for (const t of targets) {
                    const matchedDiv = findAvatarDiv(allAvatarDivs, t);
                    if (matchedDiv) {
                        nativeClick(matchedDiv);
                        clickedUnits.push(t.unitId);
                        await sleep(45);
                    } else {
                        missingUnits.push(t.unitId);
                    }
                }

                // 5. 选人完成后，将“前卫 / 中卫 / 后卫”面板全部闭合
                await collapseCharaPanels(pickerRoot);

                let searchTriggered = false;
                if (shouldSearch && clickedUnits.length > 0) {
                    await sleep(70);
                    const searchBtn = document.querySelector('button.battle_search_button')
                        || Array.from(document.querySelectorAll('button')).find(b => (b.textContent || '').replace(/\s+/g, '') === '搜索');
                    if (searchBtn) {
                        nativeClick(searchBtn);
                        searchTriggered = true;
                    }
                }

                return JSON.stringify({
                    team: targetTeamLabel,
                    clickedCount: clickedUnits.length,
                    clickedUnits: clickedUnits,
                    missingUnits: missingUnits,
                    searchTriggered: searchTriggered
                });
            })();
            """;
    }

    /// <summary>
    /// プリンセスアリーナ模式（同时选 3 个队伍）：
    /// 依次将识别出的 3 个防守队伍填入网页的【1队】、【2队】、【3队】并分别触发查询缓存
    /// </summary>
    public static string BuildSelectAllThreeTeamsScript(IReadOnlyList<IReadOnlyList<int>> threeTeams)
    {
        var spriteCoords = LoadSpriteCoordLookup();

        var teamPayloads = threeTeams
            .Take(3)
            .Select((team, idx) => new
            {
                teamLabel = $"{idx + 1}队",
                units = team
                    .Where(id => id > 0)
                    .Select(id =>
                    {
                        int base6 = id >= 100000 ? (id / 100) * 100 + 1 : id * 100 + 1;
                        (int X, int Y) coord = spriteCoords.GetValueOrDefault(base6, (-1, -1));
                        return new
                        {
                            unitId = base6,
                            spriteX = coord.X,
                            spriteY = coord.Y
                        };
                    })
                    .ToList()
            })
            .ToList();

        string payloadJson = JsonSerializer.Serialize(teamPayloads);

        return $$"""
            (async function () {
                {{SharedDomHelpersJs}}
                const teams = {{payloadJson}};

                // 1. 切换到“全属性”并展开“前卫 / 中卫 / 后卫”折叠面板
                await switchToAllAttributes(document);
                const pickerRoot = document.getElementById('battle_chara_select') || document.body;
                await expandCharaPanels(pickerRoot);

                const allAvatarDivs = Array.from(pickerRoot.querySelectorAll('div')).filter(d => {
                    return d.style && d.style.backgroundImage && d.style.backgroundImage.includes('charas');
                });

                let totalClicked = 0;

                for (let i = 0; i < teams.length; i++) {
                    const teamObj = teams[i];
                    if (!teamObj.units || teamObj.units.length === 0) continue;

                    // 切换到 1队 / 2队 / 3队 标签页
                    const tabs = Array.from(document.querySelectorAll('.ant-tabs-tab, [role="tab"]'));
                    const targetTab = tabs.find(t => (t.textContent || '').trim() === teamObj.teamLabel);
                    if (targetTab && !targetTab.classList.contains('ant-tabs-tab-active')) {
                        nativeClick(targetTab);
                        await sleep(80);
                    }

                    // 清空当前标签页槽位中已有的角色
                    const searchSelectBoxes = Array.from(document.querySelectorAll('.battle_search_select:not(.battle_search_result_ctn)'));
                    if (searchSelectBoxes.length > 0) {
                        const activeSlotBox = searchSelectBoxes[0];
                        const selectedDivs = Array.from(activeSlotBox.querySelectorAll('div')).filter(d => {
                            return getUnitIdFromReactNode(d) > 0 || (d.style && d.style.backgroundImage && d.style.backgroundImage.includes('charas'));
                        });
                        for (const selDiv of selectedDivs) {
                            nativeClick(selDiv);
                            await sleep(25);
                        }
                    }

                    // 依次点击该队伍的 5 名角色
                    for (const t of teamObj.units) {
                        const matchedDiv = findAvatarDiv(allAvatarDivs, t);
                        if (matchedDiv) {
                            nativeClick(matchedDiv);
                            totalClicked++;
                            await sleep(35);
                        }
                    }

                    // 点击“搜索”按钮，并等待当前队伍接口请求返回
                    await sleep(60);
                    const searchBtn = document.querySelector('button.battle_search_button')
                        || Array.from(document.querySelectorAll('button')).find(b => (b.textContent || '').replace(/\s+/g, '') === '搜索');
                    if (searchBtn) {
                        nativeClick(searchBtn);
                        for (let waitStep = 0; waitStep < 25; waitStep++) {
                            await sleep(100);
                            if (!searchBtn.classList.contains('ant-btn-loading')) {
                                break;
                            }
                        }
                    }
                }

                // 选人完成后将“前卫 / 中卫 / 后卫”面板全部闭合，并切回 1队 标签页展示
                await collapseCharaPanels(pickerRoot);

                const tabsAfter = Array.from(document.querySelectorAll('.ant-tabs-tab, [role="tab"]'));
                const tab1 = tabsAfter.find(t => (t.textContent || '').trim() === '1队');
                if (tab1 && !tab1.classList.contains('ant-tabs-tab-active')) {
                    nativeClick(tab1);
                }

                return JSON.stringify({
                    mode: '3v3_all_three_teams',
                    totalClicked: totalClicked
                });
            })();
            """;
    }

    /// <summary>
    /// 校验用户当前是否在 WebView 的「战绩上传」(/battle/upload) 页面，
    /// 并提取网页右侧/下方当前正在上传的防守阵容角色 UnitID 列表（供 3v3 模式自动匹配对应进攻队）
    /// </summary>
    public static string GetCheckUploadPageStateScript() => """
        (function () {
            const pathname = window.location.pathname || '';
            const titleEl = document.querySelector('.body_title');
            const titleText = titleEl ? (titleEl.textContent || '').trim() : '';
            const hasAtkBtn = Array.from(document.querySelectorAll('button')).some(b => {
                const txt = (b.textContent || '').replace(/\s+/g, '');
                return txt.includes('选择进攻阵容') || txt.includes('进攻阵容');
            });

            const isUploadPage = pathname.includes('/battle/upload') || titleText.includes('战绩上传') || hasAtkBtn;
            if (!isUploadPage) {
                return JSON.stringify({
                    isUploadPage: false,
                    defUnitIds: []
                });
            }

            let defIds = [];

            // 1. 优先从战绩上传页 React 组件实例 (state.defDetail / state.def) 获取当前防守阵容
            const candidates = [
                document.querySelector('.body_div_ctn'),
                document.getElementById('__next'),
                ...Array.from(document.querySelectorAll('.battle_search_select'))
            ];

            for (const el of candidates) {
                if (!el) continue;
                for (const key of Object.keys(el)) {
                    if (key.startsWith('__reactInternalInstance$') || key.startsWith('__reactFiber$')) {
                        let fiber = el[key];
                        for (let depth = 0; depth < 15 && fiber; depth++) {
                            const inst = fiber.stateNode;
                            if (inst && inst.state && (inst.state.def instanceof Set)) {
                                if (Array.isArray(inst.state.defDetail) && inst.state.defDetail.length > 0) {
                                    defIds = inst.state.defDetail
                                        .map(d => Number(d && d.id))
                                        .filter(id => id >= 100000);
                                }
                                if (defIds.length === 0 && inst.state.def.size > 0) {
                                    defIds = Array.from(inst.state.def)
                                        .map(Number)
                                        .filter(id => id >= 100000);
                                }
                                break;
                            }
                            fiber = fiber.return;
                        }
                    }
                    if (defIds.length > 0) break;
                }
                if (defIds.length > 0) break;
            }

            // 2. 备选：从 DOM 防守阵容槽位 (.battle_search_select 内含 lose.png) 提取 React Fiber cid
            if (defIds.length === 0) {
                const selectBoxes = Array.from(document.querySelectorAll('.battle_search_select:not(.battle_search_result_ctn)'));
                const defBox = selectBoxes.find(box => {
                    const logo = box.querySelector('.battle_search_single_logo');
                    return logo && logo.style && logo.style.backgroundImage && logo.style.backgroundImage.includes('lose');
                }) || selectBoxes[1];

                if (defBox) {
                    const divs = Array.from(defBox.querySelectorAll('div'));
                    for (const d of divs) {
                        for (const key of Object.keys(d)) {
                            if (key.startsWith('__reactInternalInstance$') || key.startsWith('__reactFiber$')) {
                                let fiber = d[key];
                                for (let depth = 0; depth < 7 && fiber; depth++) {
                                    const props = fiber.memoizedProps;
                                    if (props && typeof props.cid === 'number' && props.cid >= 100000) {
                                        if (!defIds.includes(props.cid)) {
                                            defIds.push(props.cid);
                                        }
                                        break;
                                    }
                                    fiber = fiber.return;
                                }
                            }
                        }
                    }
                }
            }

            return JSON.stringify({
                isUploadPage: true,
                defUnitIds: defIds
            });
        })();
        """;

    /// <summary>
    /// 解析 GetCheckUploadPageStateScript 的 JSON 返回结果
    /// </summary>
    public static bool TryParseUploadPageState(
        string? rawScriptResult,
        out bool isUploadPage,
        out List<int> defUnitIds)
    {
        isUploadPage = false;
        defUnitIds = [];

        if (string.IsNullOrWhiteSpace(rawScriptResult))
        {
            return false;
        }

        try
        {
            string json = rawScriptResult;
            if (json.StartsWith('"') && json.EndsWith('"'))
            {
                json = JsonSerializer.Deserialize<string>(json) ?? string.Empty;
            }

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.TryGetProperty("isUploadPage", out JsonElement isUpEl) && isUpEl.ValueKind == JsonValueKind.True)
            {
                isUploadPage = true;
            }

            if (root.TryGetProperty("defUnitIds", out JsonElement defEl) && defEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in defEl.EnumerateArray())
                {
                    if (item.TryGetInt32(out int id) && id > 0)
                    {
                        defUnitIds.Add(id);
                    }
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 在 WebView「战绩上传」(/battle/upload) 页面自动填入进攻方（及防守方）5 名角色
    /// </summary>
    public static string BuildSelectUploadAttackTeamScript(
        IReadOnlyList<int> attackUnitIds,
        IReadOnlyList<int>? defenseUnitIds = null,
        bool overwriteDefense = false)
    {
        var spriteCoords = LoadSpriteCoordLookup();

        var atkTargets = attackUnitIds
            .Where(id => id > 0)
            .Select(id =>
            {
                int base6 = id >= 100000 ? (id / 100) * 100 + 1 : id * 100 + 1;
                (int X, int Y) coord = spriteCoords.GetValueOrDefault(base6, (-1, -1));
                return new
                {
                    unitId = base6,
                    spriteX = coord.X,
                    spriteY = coord.Y
                };
            })
            .ToList();

        var defIds = (defenseUnitIds ?? [])
            .Where(id => id > 0)
            .Select(id => id >= 100000 ? (id / 100) * 100 + 1 : id * 100 + 1)
            .Distinct()
            .ToList();

        string atkJson = JsonSerializer.Serialize(atkTargets);
        string defJson = JsonSerializer.Serialize(defIds);

        return $$"""
            (async function () {
                {{SharedDomHelpersJs}}
                const targets = {{atkJson}};
                const fallbackDefIds = {{defJson}};
                const shouldOverwriteDef = {{(overwriteDefense ? "true" : "false")}};

                // 寻找 /battle/upload 页面组件实例
                function findUploadComponentInstance() {
                    const candidates = [
                        document.querySelector('.body_div_ctn'),
                        document.getElementById('__next'),
                        ...Array.from(document.querySelectorAll('.battle_search_select'))
                    ];
                    for (const el of candidates) {
                        if (!el) continue;
                        for (const key of Object.keys(el)) {
                            if (key.startsWith('__reactInternalInstance$') || key.startsWith('__reactFiber$')) {
                                let fiber = el[key];
                                for (let depth = 0; depth < 15 && fiber; depth++) {
                                    const inst = fiber.stateNode;
                                    if (inst && inst.state && (inst.state.atk instanceof Set) && typeof inst.showSelectModal === 'function') {
                                        return inst;
                                    }
                                    fiber = fiber.return;
                                }
                            }
                        }
                    }
                    return null;
                }

                const uploadInst = findUploadComponentInstance();
                if (uploadInst) {
                    const atkUnitIds = targets.map(t => t.unitId);
                    const nextAtkSet = new Set(atkUnitIds);

                    // 通过原生组件 showSelectModal(null) 自动触发站位排序并生成带满星/专武的 atkDetail
                    await new Promise(resolve => {
                        uploadInst.setState({ showModal: 'atk', atk: nextAtkSet }, () => {
                            uploadInst.showSelectModal(null);
                            resolve();
                        });
                    });

                    // 填入防守阵容（当明确指定覆盖，或当前页面防守阵容为空时）
                    if ((shouldOverwriteDef || !uploadInst.state.def || uploadInst.state.def.size === 0) && fallbackDefIds.length > 0) {
                        const nextDefSet = new Set(fallbackDefIds);
                        await new Promise(resolve => {
                            uploadInst.setState({ showModal: 'def', def: nextDefSet }, () => {
                                uploadInst.showSelectModal(null, true);
                                resolve();
                            });
                        });
                    }

                    return JSON.stringify({
                        mode: 'react_state',
                        isLoggedIn: !!(uploadInst.props && uploadInst.props.user && uploadInst.props.user.isLogin),
                        selectedAttackUnits: atkUnitIds,
                        selectedDefenseUnits: fallbackDefIds
                    });
                }

                // 备选 DOM 模拟点击流程：点击「选择进攻阵容」打开弹窗 -> 展开前中后卫 -> 依次点击 5 名角色 -> 点击「确定」
                const atkBtn = Array.from(document.querySelectorAll('button')).find(b => {
                    const txt = (b.textContent || '').replace(/\s+/g, '');
                    return txt.includes('选择进攻阵容');
                });

                if (atkBtn && !atkBtn.disabled) {
                    nativeClick(atkBtn);
                    await sleep(150);
                }

                const modalRoot = document.querySelector('.battle_upload_unit_select_modal') || document.body;
                await switchToAllAttributes(modalRoot);
                await expandCharaPanels(modalRoot);

                // 先点击弹窗内的“重新选择”清空旧进攻阵容
                const reselectBtn = Array.from(modalRoot.querySelectorAll('.buttons button, button')).find(b => {
                    const txt = (b.textContent || '').replace(/\s+/g, '');
                    return txt.includes('重新选择') || txt.includes('重选');
                });
                if (reselectBtn) {
                    nativeClick(reselectBtn);
                    await sleep(40);
                }

                const allAvatarDivs = Array.from(modalRoot.querySelectorAll('div')).filter(d => {
                    return d.style && d.style.backgroundImage && d.style.backgroundImage.includes('charas');
                });

                const clickedUnits = [];
                for (const t of targets) {
                    const matchedDiv = findAvatarDiv(allAvatarDivs, t);
                    if (matchedDiv) {
                        nativeClick(matchedDiv);
                        clickedUnits.push(t.unitId);
                        await sleep(35);
                    }
                }

                await collapseCharaPanels(modalRoot);

                const confirmBtn = modalRoot.querySelector('button.backbutton')
                    || Array.from(modalRoot.querySelectorAll('button')).find(b => (b.textContent || '').replace(/\s+/g, '') === '确定');
                if (confirmBtn) {
                    nativeClick(confirmBtn);
                }

                return JSON.stringify({
                    mode: 'dom_modal',
                    selectedAttackUnits: clickedUnits
                });
            })();
            """;
    }

    private static Dictionary<int, (int X, int Y)> LoadSpriteCoordLookup()
    {
        var dict = new Dictionary<int, (int X, int Y)>();
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "sprites_map.json");
            if (!File.Exists(path))
            {
                return dict;
            }

            string json = File.ReadAllText(path);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("star3", out JsonElement star3))
            {
                foreach (JsonProperty prop in star3.EnumerateObject())
                {
                    if (int.TryParse(prop.Name, out int unitId))
                    {
                        int x = prop.Value.TryGetProperty("x", out var xp) ? xp.GetInt32() : -1;
                        int y = prop.Value.TryGetProperty("y", out var yp) ? yp.GetInt32() : -1;
                        dict[unitId] = (x, y);
                    }
                }
            }
        }
        catch
        {
        }

        return dict;
    }
}
