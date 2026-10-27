using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;

namespace PremiereCalendar.BrowserTests;

[Collection(BrowserAppCollection.Name)]
public sealed class ReconnectRecoveryTests(BrowserAppFixture application) : PageTest
{
    [Fact]
    public async Task Failed_reconnect_retries_while_visible_then_recovers_on_online_event()
    {
        await PrepareRecoveryPageAsync();
        await Page.EvaluateAsync("""
            window.attempts = 0;
            window.Blazor = { reconnect: async () => { attempts++; throw new Error('offline'); } };
            document.getElementById('components-reconnect-modal').dispatchEvent(
                new CustomEvent('components-reconnect-state-changed', { detail: { state: 'failed' } }));
            """);

        for (var attempt = 1; attempt <= 12; attempt++)
        {
            await Page.Clock.FastForwardAsync(5_000);
            Assert.Equal(attempt, await Page.EvaluateAsync<int>("attempts"));
        }
        await Page.Clock.FastForwardAsync(60_000);
        Assert.Equal(12, await Page.EvaluateAsync<int>("attempts"));

        await Page.EvaluateAsync("""
            Blazor.reconnect = async () => { attempts++; return true; };
            window.dispatchEvent(new Event('online'));
            """);
        await Page.WaitForFunctionAsync("attempts === 13");
        await Page.Clock.FastForwardAsync(60_000);
        Assert.Equal(13, await Page.EvaluateAsync<int>("attempts"));
    }

    [Fact]
    public async Task Hidden_page_waits_for_visibility_and_prevents_overlapping_reconnects()
    {
        await PrepareRecoveryPageAsync();
        await Page.EvaluateAsync("""
            window.testVisibility = 'hidden';
            Object.defineProperty(document, 'visibilityState', { get: () => testVisibility });
            window.attempts = 0;
            window.Blazor = { reconnect: () => { attempts++; return new Promise(resolve => window.finishReconnect = resolve); } };
            document.getElementById('components-reconnect-modal').dispatchEvent(
                new CustomEvent('components-reconnect-state-changed', { detail: { state: 'failed' } }));
            """);
        await Page.Clock.FastForwardAsync(60_000);
        Assert.Equal(0, await Page.EvaluateAsync<int>("attempts"));

        await Page.EvaluateAsync("""
            testVisibility = 'visible';
            document.dispatchEvent(new Event('visibilitychange'));
            window.dispatchEvent(new Event('online'));
            document.getElementById('components-reconnect-button').click();
            """);
        Assert.Equal(1, await Page.EvaluateAsync<int>("attempts"));
        await Page.EvaluateAsync("finishReconnect(true)");
        await Page.Clock.FastForwardAsync(60_000);
        Assert.Equal(1, await Page.EvaluateAsync<int>("attempts"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejected_circuit_reloads_the_page(bool reconnectReturnsFalse)
    {
        await PrepareRecoveryPageAsync();
        var navigation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Page.FrameNavigated += (_, frame) =>
        {
            if (frame == Page.MainFrame) navigation.TrySetResult();
        };
        await Page.EvaluateAsync("window.Blazor = { reconnect: async () => false };");
        await Page.EvaluateAsync("""
            reconnectReturnsFalse => {
                if (reconnectReturnsFalse) document.getElementById('components-reconnect-button').click();
                else document.getElementById('components-reconnect-modal').dispatchEvent(
                    new CustomEvent('components-reconnect-state-changed', { detail: { state: 'rejected' } }));
            }
            """, reconnectReturnsFalse);
        await navigation.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private async Task PrepareRecoveryPageAsync()
    {
        var response = await Page.APIRequest.GetAsync($"{application.BaseUrl}/Components/Layout/ReconnectModal.razor.js");
        Assert.True(response.Ok);
        var script = await response.TextAsync();
        await Page.Clock.InstallAsync();
        await Page.SetContentAsync("""
            <dialog id="components-reconnect-modal"></dialog>
            <button id="components-reconnect-button">Retry</button>
            <button id="components-resume-button">Resume</button>
            """);
        await Page.AddScriptTagAsync(new() { Content = script });
    }
}
