using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using ScanRelay.Core.Audit;
using ScanRelay.Core.Cloud;
using ScanRelay.Core.Data;
using ScanRelay.Core.Modules;
using ScanRelay.Core.Settings;

namespace ScanRelay.Admin.Components.Shared;

/// <summary>What every page needs: who is asking, a database, and one way to show success or failure.</summary>
public abstract class PageBase : ComponentBase, IDisposable
{
    private Timer? _timer;

    [Inject] protected IDbContextFactory<ScanRelayDbContext> Db { get; set; } = null!;
    [Inject] protected CurrentUser User { get; set; } = null!;
    [Inject] protected NavigationManager Nav { get; set; } = null!;
    [Inject] protected TimeProvider Clock { get; set; } = null!;

    protected Actor Actor { get; private set; } = Actor.Setup;
    protected bool IsAdmin { get; private set; }
    protected bool Busy { get; private set; }
    protected string? Notice { get; set; }
    protected string? Error { get; set; }
    protected DateTime NowUtc => Clock.GetUtcNow().UtcDateTime;

    /// <summary>Set to reload the page's data on a timer, so it stays current without pressing anything.</summary>
    protected virtual TimeSpan? RefreshEvery => null;

    protected virtual Task LoadAsync() => Task.CompletedTask;

    protected sealed override async Task OnInitializedAsync()
    {
        Actor = await User.ActorAsync();
        IsAdmin = await User.IsAdminAsync();
        await SafeLoadAsync();
        if (RefreshEvery is { } every)
        {
            _timer = new Timer(_ => InvokeAsync(async () =>
            {
                if (Busy) return;
                await SafeLoadAsync();
                StateHasChanged();
            }), null, every, every);
        }
    }

    /// <summary>Runs an action from a button, showing what happened. Reloads the page's data afterwards.</summary>
    protected async Task DoAsync(Func<Task<string?>> action, string? success = null)
    {
        if (Busy) return;
        Busy = true;
        Notice = Error = null;
        StateHasChanged();
        try
        {
            var problem = await action();
            if (problem is null) Notice = success;
            else Error = problem;
        }
        catch (Exception e) when (e is CloudException or SettingsException or ModuleException or DbUpdateException or IOException or InvalidOperationException)
        {
            Error = e is SettingsException s ? string.Join(" ", s.Problems) : e.Message;
        }
        finally
        {
            Busy = false;
        }

        await SafeLoadAsync();
    }

    protected Task DoAsync(Func<Task> action, string? success = null) => DoAsync(
        async () =>
        {
            await action();
            return null;
        },
        success);

    private async Task SafeLoadAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception e) when (e is CloudException or DbUpdateException or Microsoft.Data.Sqlite.SqliteException)
        {
            Error = e.Message;
        }
    }

    public virtual void Dispose()
    {
        _timer?.Dispose();
        GC.SuppressFinalize(this);
    }
}
