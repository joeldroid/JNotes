using JNotes.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace JNotes.Tests.Integration;

public class SettingsServiceTests : IAsyncLifetime
{
    private CoreServicesFixture _fixture = null!;

    public async Task InitializeAsync() => _fixture = await CoreServicesFixture.CreateAsync();

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    private ISettingsService Settings => _fixture.Scope.ServiceProvider.GetRequiredService<ISettingsService>();

    [Fact]
    public async Task TabbedView_defaults_to_off()
    {
        Assert.False(await Settings.GetTabbedViewAsync(_fixture.UserAId));
    }

    [Fact]
    public async Task TabbedView_round_trips_and_can_be_turned_back_off()
    {
        await Settings.SetTabbedViewAsync(_fixture.UserAId, true);
        Assert.True(await Settings.GetTabbedViewAsync(_fixture.UserAId));

        await Settings.SetTabbedViewAsync(_fixture.UserAId, false);
        Assert.False(await Settings.GetTabbedViewAsync(_fixture.UserAId));
    }

    [Fact]
    public async Task TabbedView_is_per_user_and_independent_of_theme()
    {
        await Settings.SetTabbedViewAsync(_fixture.UserAId, true);
        await Settings.SetThemeAsync(_fixture.UserAId, AppTheme.Light);

        Assert.True(await Settings.GetTabbedViewAsync(_fixture.UserAId));
        Assert.False(await Settings.GetTabbedViewAsync(_fixture.UserBId));
        Assert.Equal(AppTheme.Dark, await Settings.GetThemeAsync(_fixture.UserBId));
    }

    [Fact]
    public async Task ImageCleanupInterval_defaults_to_seven_days()
    {
        Assert.Equal(7, await Settings.GetImageCleanupIntervalDaysAsync());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(0)] // 0 = off, and must not be mistaken for "unset → default"
    public async Task ImageCleanupInterval_round_trips(int days)
    {
        await Settings.SetImageCleanupIntervalDaysAsync(days);

        Assert.Equal(days, await Settings.GetImageCleanupIntervalDaysAsync());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(366)]
    public async Task ImageCleanupInterval_rejects_an_out_of_range_value_and_keeps_the_old_one(int days)
    {
        await Settings.SetImageCleanupIntervalDaysAsync(3);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Settings.SetImageCleanupIntervalDaysAsync(days));

        Assert.Equal(3, await Settings.GetImageCleanupIntervalDaysAsync());
    }

    [Fact]
    public async Task ImageCleanupInterval_is_installation_wide_not_per_user()
    {
        _fixture.ActAsUserA();
        await Settings.SetImageCleanupIntervalDaysAsync(14);

        _fixture.ActAsUserB();

        Assert.Equal(14, await Settings.GetImageCleanupIntervalDaysAsync());
    }

    [Fact]
    public async Task ImageCleanupLastRun_is_null_until_set_then_round_trips()
    {
        Assert.Null(await Settings.GetImageCleanupLastRunUtcAsync());

        var when = new DateTimeOffset(2026, 9, 1, 8, 30, 15, TimeSpan.Zero);
        await Settings.SetImageCleanupLastRunUtcAsync(when);

        Assert.Equal(when, await Settings.GetImageCleanupLastRunUtcAsync());
    }
}
