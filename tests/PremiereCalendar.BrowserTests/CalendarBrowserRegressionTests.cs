using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;

namespace PremiereCalendar.BrowserTests;

[Collection(BrowserAppCollection.Name)]
public sealed class CalendarBrowserRegressionTests(BrowserAppFixture application, ITestOutputHelper output) : PageTest
{
    [Theory]
    [InlineData(1440, 1000)]
    [InlineData(390, 844)]
    public async Task Calendar_navigation_accessibility_and_performance_contract(int width, int height)
    {
        await Page.SetViewportSizeAsync(width, height);
        await Context.Tracing.StartAsync(new() { Screenshots = false, Snapshots = false, Sources = true });
        output.WriteLine("Performance trace: actions/network/sources enabled; continuous screenshots/DOM snapshots disabled; failure HTML/full-page PNG retained.");
        var consoleErrors = new List<string>();
        var pageErrors = new List<string>();
        Page.Console += (_, message) => { if (message.Type == "error") consoleErrors.Add(message.Text); };
        Page.PageError += (_, error) => pageErrors.Add(error);
        await Page.AddInitScriptAsync("""
            window.__pcLongTasks = [];
            window.__pcCls = 0;
            window.__pcWindowTransitions = [];
            window.__pcMatchesWindow = expected => {
                const day = document.querySelector('[data-testid="calendar-day"]');
                if (!day || day.dataset.dayWindowPending !== 'false') return false;
                const cards = [...day.querySelectorAll('[data-testid="premiere-card"]')];
                return cards.length > 0 && cards.length <= 40
                    && Number(day.dataset.dayWindowStart) === expected.start
                    && Number(day.dataset.dayWindowEnd) === expected.end
                    && Number(day.dataset.dayWindowTotal) === expected.total
                    && cards.length === expected.ids.length
                    && cards.every((card, index) => card.dataset.canonicalId === expected.ids[index]);
            };
            new PerformanceObserver(list => { for (const e of list.getEntries()) window.__pcLongTasks.push(e.duration); }).observe({type:'longtask', buffered:true});
            new PerformanceObserver(list => { for (const e of list.getEntries()) if (!e.hadRecentInput) window.__pcCls += e.value; }).observe({type:'layout-shift', buffered:true});
            window.__pcObserveDaySwitch = () => {
                const started = Number(sessionStorage.getItem('pc-day-switch-started'));
                const target = sessionStorage.getItem('pc-day-switch-target');
                if (!Number.isFinite(started) || !target) return;
                const recordWhenUsable = () => {
                    const selected = [...document.querySelectorAll('[data-day-button][aria-selected="true"]')]
                        .some(candidate => candidate.getAttribute('data-day-target') === target);
                    if (!selected || !document.querySelector('[data-testid="premiere-card"]')) return false;
                    sessionStorage.setItem('pc-day-switch-duration', (Date.now() - started).toString());
                    return true;
                };
                if (recordWhenUsable()) return;
                const observer = new MutationObserver(() => {
                    if (recordWhenUsable()) observer.disconnect();
                });
                observer.observe(document.documentElement, { attributes: true, childList: true, subtree: true });
            };
            addEventListener('DOMContentLoaded', window.__pcObserveDaySwitch);
            """);

        try
        {
            var firstCardTimer = Stopwatch.StartNew();
            await Page.GotoAsync(application.BaseUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Page.Locator("[data-testid='premiere-card']").First.WaitForAsync(new() { Timeout = 5_000 });
            Assert.True(firstCardTimer.Elapsed <= TimeSpan.FromSeconds(5), $"First usable card took {firstCardTimer.Elapsed}.");
            output.WriteLine($"First usable card: {firstCardTimer.ElapsedMilliseconds} ms ({width}x{height}).");
            Assert.Equal("compact", await Page.Locator("html").GetAttributeAsync("data-density"));

            await Expect(Page.Locator(".card-description").First).ToBeVisibleAsync();
            await Expect(Page.Locator(".primary-actions").First.GetByRole(AriaRole.Link, new() { Name = "Trailer", Exact = true })).ToBeVisibleAsync();
            await Expect(Page.Locator(".primary-actions").First.GetByRole(AriaRole.Link, new() { Name = "YouTube search", Exact = true })).ToBeVisibleAsync();
            await Page.GetByRole(AriaRole.Button, new() { Name = "Open tools and actions" }).ClickAsync();
            await Page.GetByRole(AriaRole.Button, new() { Name = "Use comfortable cards" }).ClickAsync();
            await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-density", "comfortable");

            foreach (var route in new[] { "series", "movies", "" })
            {
                await Page.GotoAsync($"{application.BaseUrl}/{route}");
                await Page.Locator("[data-testid='premiere-card']").First.WaitForAsync(new() { Timeout = 5_000 });
                await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-density", "comfortable");
            }

            await Page.Locator(".filter-open-button").ClickAsync();
            await Expect(Page.Locator("[data-testid='filter-pane']")).ToBeVisibleAsync();
            await Page.GetByRole(AriaRole.Combobox, new() { Name = "Sort results by" }).SelectOptionAsync("Title");
            await Page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
            await Expect(Page).ToHaveURLAsync(new Regex(@"[?&]sort=title(?:&|$)"));

            var targetDay = Page.Locator("[data-day-button]").Nth(1);
            await targetDay.WaitForAsync();
            var targetDayId = await targetDay.GetAttributeAsync("data-day-target");
            Assert.NotNull(targetDayId);
            var documentTimeOrigin = await Page.EvaluateAsync<double>("performance.timeOrigin");
            await Page.EvaluateAsync("""
                target => {
                    const button = [...document.querySelectorAll('[data-day-button]')]
                        .find(candidate => candidate.getAttribute('data-day-target') === target);
                    if (!button) throw new Error(`Day button ${target} was not found.`);
                    sessionStorage.removeItem('pc-day-switch-duration');
                    sessionStorage.setItem('pc-day-switch-target', target);
                    sessionStorage.setItem('pc-day-switch-started', Date.now().toString());
                    window.__pcObserveDaySwitch();
                    button.click();
                }
                """, targetDayId);
            await Expect(Page.Locator("[data-day-button][aria-selected='true']")).ToHaveAttributeAsync(
                "data-day-target", targetDayId);
            await Page.Locator("[data-testid='premiere-card']").First.WaitForAsync();
            await Page.WaitForFunctionAsync("Number.isFinite(Number(sessionStorage.getItem('pc-day-switch-duration')))");
            var daySwitchMilliseconds = await Page.EvaluateAsync<double>("Number(sessionStorage.getItem('pc-day-switch-duration'))");
            Assert.Equal(documentTimeOrigin, await Page.EvaluateAsync<double>("performance.timeOrigin"));
            output.WriteLine($"Day switch: {daySwitchMilliseconds:0} ms ({width}x{height}); document preserved.");
            Assert.True(daySwitchMilliseconds <= 750, $"Day switch took {daySwitchMilliseconds:0} ms.");

            var selectedDay = DateOnly.ParseExact(targetDayId["premiere-day-".Length..], "yyyyMMdd", CultureInfo.InvariantCulture);
            var orderedIds = ExpectedDefaultTitleOrder(selectedDay);
            Assert.Equal(48, orderedIds.Length);
            await Page.WaitForFunctionAsync("expected => window.__pcMatchesWindow(expected)",
                Window(orderedIds, 0, 10), new() { Timeout = 5_000 });
            // Establish enough items to require a trimmed window and Previous,
            // independently of incidental viewport autoloading.
            await MoveWindowAsync("more", orderedIds, 0, 20);
            await Page.EvaluateAsync("window.__pcWindowTransitions = []");

            var rollingWindow = Stopwatch.StartNew();
            await MoveWindowAsync("more", orderedIds, 0, 30);
            await MoveWindowAsync("more", orderedIds, 0, 40);
            await MoveWindowAsync("more", orderedIds, 8, 48);
            await MoveWindowAsync("previous", orderedIds, 0, 40);
            Assert.InRange(await Page.Locator("[data-testid='premiere-card']").CountAsync(), 1, 40);
            rollingWindow.Stop();
            output.WriteLine($"Committed window DOM-update wall: {rollingWindow.ElapsedMilliseconds} ms ({width}x{height}); required 3 forward + 1 previous completed.");
            output.WriteLine(await Page.EvaluateAsync<string>("JSON.stringify(window.__pcWindowTransitions)"));
            Assert.True(rollingWindow.ElapsedMilliseconds <= 3_000, $"Rolling-window navigation took {rollingWindow.ElapsedMilliseconds} ms.");

            // Exercise the real scroll/IntersectionObserver path separately from
            // the four committed DOM updates above. No button or observer is
            // invoked by the test during these two transitions.
            await VerifyScrollAutoloadAsync(orderedIds);

            var initialWeek = await Page.Locator("[data-testid='week-range']").InnerTextAsync();
            await Page.GetByRole(AriaRole.Button, new() { Name = "Next week" }).ClickAsync();
            await Expect(Page.Locator("[data-testid='week-range']")).Not.ToHaveTextAsync(initialWeek);
            await Page.Locator("[data-testid='premiere-card']").First.WaitForAsync(new() { Timeout = 5_000 });
            await Page.GetByRole(AriaRole.Button, new() { Name = "Previous week" }).ClickAsync();
            await Expect(Page.Locator("[data-testid='week-range']")).ToHaveTextAsync(initialWeek);

            await Page.GotoAsync($"{application.BaseUrl}/settings");
            await Expect(Page.Locator("[data-testid='local-status-center']")).ToBeVisibleAsync();
            await Expect(Page.GetByRole(AriaRole.Article, new() { Name = "View sync settings" })).ToBeVisibleAsync();
            await Page.GetByRole(AriaRole.Textbox, new() { Name = "New view sync group name" }).FillAsync($"Browser {width}");
            await Page.GetByRole(AriaRole.Button, new() { Name = "Create view sync group" }).ClickAsync();
            await Expect(Page.GetByRole(AriaRole.Article, new() { Name = $"View sync group Browser {width}" })).ToBeVisibleAsync();
            await Page.GetByRole(AriaRole.Checkbox, new() { Name = "Sync viewing on this browser" }).CheckAsync();
            await Page.GetByRole(AriaRole.Button, new() { Name = "Save view sync settings" }).ClickAsync();
            await Page.GotoAsync(application.BaseUrl);
            await Page.Locator("[data-testid='premiere-card']").First.WaitForAsync();

            var seriousImpacts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "critical", "serious" };
            var axe = await Page.RunAxe();
            Assert.DoesNotContain(axe.Violations, violation => seriousImpacts.Contains(violation.Impact ?? ""));
            var mountedCards = await Page.Locator("[data-testid='premiere-card']").CountAsync();
            var domNodes = await Page.Locator("*").CountAsync();
            Assert.InRange(mountedCards, 1, 40);
            Assert.True(domNodes < 2_000, $"DOM contains {domNodes} nodes.");
            var overflow = await Page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth + 1");
            Assert.False(overflow, "Page has horizontal overflow.");
            var cls = await Page.EvaluateAsync<double>("window.__pcCls");
            var longestTask = await Page.EvaluateAsync<double>("Math.max(0, ...window.__pcLongTasks)");
            Assert.True(cls <= 0.1, $"CLS was {cls:0.###}.");
            Assert.True(longestTask <= 500, $"Longest browser task was {longestTask:0} ms.");
            Assert.Empty(consoleErrors);
            Assert.Empty(pageErrors);
            Assert.Empty(await Page.Locator("[data-testid='loading']").AllAsync());
            await Context.Tracing.StopAsync();
        }
        catch
        {
            var artifactRoot = Path.Combine(AppContext.BaseDirectory, "TestResults", $"browser-{width}x{height}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(artifactRoot);
            await Page.ScreenshotAsync(new() { Path = Path.Combine(artifactRoot, "page.png"), FullPage = true });
            await File.WriteAllTextAsync(Path.Combine(artifactRoot, "page.html"), await Page.ContentAsync(), TestContext.Current.CancellationToken);
            await Context.Tracing.StopAsync(new() { Path = Path.Combine(artifactRoot, "trace.zip") });
            throw;
        }
    }

    [Theory]
    [InlineData("pending", 3)]
    [InlineData("non-trim", 3)]
    [InlineData("sentinel-leaves-view", 1)]
    public async Task Autoload_resumes_after_a_window_move_only_while_the_sentinel_stays_visible(string scenario, int expectedClicks)
    {
        await Page.GotoAsync(application.BaseUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Page.Locator("[data-testid='premiere-card']").First.WaitForAsync(new() { Timeout = 10_000 });
        // A synthetic day whose load control mimics the component. "pending": each click disables the control
        // and settles later. "non-trim": the move completes without ever toggling pending, only the window end
        // changes. "sentinel-leaves-view": the sentinel scrolls away after the first load. The sentinel stays in
        // view otherwise, so the IntersectionObserver reports no further change and only the settle signal can
        // resume loading.
        var clicks = await Page.EvaluateAsync<int>("""
            scenario => new Promise(resolve => {
                const day = document.createElement('section');
                day.setAttribute('data-testid', 'calendar-day');
                day.dataset.dayWindowPending = 'false';
                day.dataset.dayWindowStart = '0';
                day.dataset.dayWindowEnd = '8';
                day.style.cssText = 'position:fixed;top:0;left:0;z-index:-1;opacity:0';
                day.innerHTML = '<button type="button" data-day-load-more>more</button><div data-day-autoload-sentinel>sentinel</div>';
                const button = day.querySelector('button');
                const sentinel = day.querySelector('[data-day-autoload-sentinel]');
                let clicks = 0;
                button.addEventListener('click', () => {
                    clicks++;
                    if (scenario === 'non-trim') {
                        setTimeout(() => { day.dataset.dayWindowEnd = String(8 + clicks * 8); }, 20);
                        return;
                    }
                    day.dataset.dayWindowPending = 'true';
                    button.disabled = true;
                    setTimeout(() => {
                        if (scenario === 'sentinel-leaves-view') sentinel.style.cssText = 'position:fixed;top:20000px';
                        button.disabled = false;
                        day.dataset.dayWindowPending = 'false';
                    }, 50);
                });
                document.body.appendChild(day);
                const expected = scenario === 'sentinel-leaves-view' ? 1 : 3;
                const finish = () => { day.remove(); resolve(clicks); };
                const deadline = performance.now() + (expected === 1 ? 1500 : 8000);
                const poll = () => {
                    if ((expected > 1 && clicks >= expected) || performance.now() > deadline) finish();
                    else setTimeout(poll, 25);
                };
                poll();
            })
            """, scenario);
        if (expectedClicks == 1)
        {
            Assert.Equal(1, clicks);
        }
        else
        {
            Assert.True(clicks >= expectedClicks, $"Autoload stalled after {clicks} load(s) ({scenario}) while the sentinel stayed in view.");
        }
    }

    private async Task MoveWindowAsync(string direction, string[] orderedIds, int start, int end) =>
        await Page.EvaluateAsync("""
            request => new Promise((resolve, reject) => {
                const day = document.querySelector('[data-testid="calendar-day"]');
                const button = day?.querySelector(`[data-day-load-${request.direction}]`);
                if (!(button instanceof HTMLButtonElement) || button.disabled || day.dataset.dayWindowPending !== 'false') {
                    reject(new Error(`Required ${request.direction} control is unavailable or pending.`));
                    return;
                }
                const transition = { direction: request.direction, start: request.start, end: request.end,
                    ids: request.ids, dispatchMs: performance.now(), completedMs: null };
                window.__pcWindowTransitions.push(transition);
                const observer = new MutationObserver(check);
                const timeout = setTimeout(() => {
                    observer.disconnect();
                    reject(new Error(`Window ${request.start}-${request.end} did not complete within 5000 ms.`));
                }, 5000);
                function check() {
                    const count = day.querySelectorAll('[data-testid="premiere-card"]').length;
                    if (count > 40) {
                        clearTimeout(timeout);
                        observer.disconnect();
                        reject(new Error(`Window mounted ${count} cards.`));
                    } else if (window.__pcMatchesWindow(request)) {
                        transition.completedMs = performance.now();
                        clearTimeout(timeout);
                        observer.disconnect();
                        resolve();
                    }
                }
                observer.observe(day, { childList: true, subtree: true, attributes: true,
                    attributeFilter: ['data-day-window-start', 'data-day-window-end', 'data-day-window-total', 'data-day-window-pending'] });
                button.click();
                check();
            })
            """, Window(orderedIds, start, end, direction));

    private async Task VerifyScrollAutoloadAsync(string[] orderedIds)
    {
        var forwardSentinel = Page.Locator("[data-day-autoload-sentinel]");
        await Expect(forwardSentinel).ToHaveAttributeAsync("data-autoload-observed", "true", new() { Timeout = 5_000 });
        Assert.True(await Page.EvaluateAsync<bool>("window.premiereCalendarDayAutoloadObserver instanceof IntersectionObserver"));
        var beforeScroll = await Page.EvaluateAsync<double>("window.scrollY");
        await forwardSentinel.ScrollIntoViewIfNeededAsync(new() { Timeout = 5_000 });
        await Page.WaitForFunctionAsync("expected => window.__pcMatchesWindow(expected)",
            Window(orderedIds, 8, 48), new() { Timeout = 5_000 });
        var forwardScroll = await Page.EvaluateAsync<double>("window.scrollY");
        Assert.True(forwardScroll > beforeScroll, "Forward autoload must follow an actual downward page scroll.");

        var previousSentinel = Page.Locator("[data-day-window-previous]");
        await Expect(previousSentinel).ToHaveAttributeAsync("data-autoload-observed", "true", new() { Timeout = 5_000 });
        await previousSentinel.ScrollIntoViewIfNeededAsync(new() { Timeout = 5_000 });
        await Page.WaitForFunctionAsync("expected => window.__pcMatchesWindow(expected)",
            Window(orderedIds, 0, 40), new() { Timeout = 5_000 });
        var previousScroll = await Page.EvaluateAsync<double>("window.scrollY");
        Assert.True(previousScroll < forwardScroll, "Previous restoration must follow an actual upward page scroll.");
        output.WriteLine($"Real-scroll autoload: 1-40 -> 9-48 -> 1-40; ordered IDs verified, <=40 cards each; scrollY {beforeScroll:0} -> {forwardScroll:0} -> {previousScroll:0}.");
    }

    private static object Window(string[] orderedIds, int start, int end, string direction = "more") =>
        new { start, end, total = orderedIds.Length, ids = orderedIds[start..end], direction };

    private static string[] ExpectedDefaultTitleOrder(DateOnly day)
    {
        // The deterministic provider emits 72 items/day. Default filters retain
        // movies and new-series premieres; title ordering uses ordinal comparison.
        var dayOffset = day.DayNumber - PremiereCalendar.Models.CalendarFilters.StartOfWeek(day).DayNumber;
        return Enumerable.Range(0, 72)
            .Where(index => index % 3 == 0 || index % 2 == 0)
            .OrderBy(index => $"Fixture {(index % 3 == 0 ? "Movie" : "Series")} {dayOffset * 72 + index + 1}", StringComparer.OrdinalIgnoreCase)
            .Select(index => $"browser:{day:yyyyMMdd}:{index}")
            .ToArray();
    }
}
