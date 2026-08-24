using System.Diagnostics;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;

namespace PremiereCalendar.BrowserTests;

[Collection(BrowserAppCollection.Name)]
public sealed class CalendarBrowserRegressionTests(BrowserAppFixture application) : PageTest
{
    [Theory]
    [InlineData(1440, 1000)]
    [InlineData(390, 844)]
    public async Task Calendar_navigation_accessibility_and_performance_contract(int width, int height)
    {
        await Page.SetViewportSizeAsync(width, height);
        await Context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });
        var consoleErrors = new List<string>();
        var pageErrors = new List<string>();
        Page.Console += (_, message) => { if (message.Type == "error") consoleErrors.Add(message.Text); };
        Page.PageError += (_, error) => pageErrors.Add(error);
        await Page.AddInitScriptAsync("""
            window.__pcLongTasks = [];
            window.__pcCls = 0;
            new PerformanceObserver(list => { for (const e of list.getEntries()) window.__pcLongTasks.push(e.duration); }).observe({type:'longtask', buffered:true});
            new PerformanceObserver(list => { for (const e of list.getEntries()) if (!e.hadRecentInput) window.__pcCls += e.value; }).observe({type:'layout-shift', buffered:true});
            """);

        try
        {
            var firstCardTimer = Stopwatch.StartNew();
            await Page.GotoAsync(application.BaseUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Page.Locator("[data-testid='premiere-card']").First.WaitForAsync(new() { Timeout = 5_000 });
            Assert.True(firstCardTimer.Elapsed <= TimeSpan.FromSeconds(5), $"First usable card took {firstCardTimer.Elapsed}.");
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
            await Page.WaitForURLAsync(url => url.Contains("sort=title", StringComparison.OrdinalIgnoreCase));

            var daySwitch = Stopwatch.StartNew();
            await Page.Locator("[data-day-button]").Nth(1).ClickAsync();
            await Page.Locator("[data-testid='premiere-card']").First.WaitForAsync();
            Assert.True(daySwitch.ElapsedMilliseconds <= 750, $"Day switch took {daySwitch.ElapsedMilliseconds} ms.");

            var rollingWindow = Stopwatch.StartNew();
            for (var movement = 0; movement < 3; movement++)
            {
                var loadMore = Page.Locator("[data-day-load-more]").First;
                if (await loadMore.IsVisibleAsync())
                {
                    await loadMore.ClickAsync(new() { Force = true });
                    await WaitForUsableCardWindowAsync();
                }
            }
            var loadPrevious = Page.Locator("[data-day-load-previous]").First;
            if (await loadPrevious.IsVisibleAsync())
            {
                await loadPrevious.ClickAsync(new() { Force = true });
                await WaitForUsableCardWindowAsync();
            }
            await WaitForUsableCardWindowAsync();
            Assert.InRange(await Page.Locator("[data-testid='premiere-card']").CountAsync(), 1, 40);
            Assert.True(rollingWindow.ElapsedMilliseconds <= 3_000, $"Rolling-window navigation took {rollingWindow.ElapsedMilliseconds} ms.");

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
            await File.WriteAllTextAsync(Path.Combine(artifactRoot, "page.html"), await Page.ContentAsync());
            await Context.Tracing.StopAsync(new() { Path = Path.Combine(artifactRoot, "trace.zip") });
            throw;
        }
    }

    private async Task WaitForUsableCardWindowAsync() =>
        await Page.WaitForFunctionAsync(
            "document.querySelectorAll(\"[data-testid='premiere-card']\").length > 0 && document.querySelectorAll(\"[data-testid='premiere-card']\").length <= 40",
            null,
            new() { Timeout = 5_000 });
}
