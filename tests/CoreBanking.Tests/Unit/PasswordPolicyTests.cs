using CoreBanking.Application.Features.Users;

namespace CoreBanking.Tests.Unit;

public class PasswordPolicyTests
{
    private static readonly TimeSpan Noon = TimeSpan.FromHours(12);

    [Fact]
    public void Active_unrestricted_user_is_not_blocked() =>
        Assert.False(UserAccessPolicy.IsBlocked(new UserProfile { IsActive = true }, "10.0.0.5", Noon));

    [Fact]
    public void Deactivated_user_is_blocked() =>
        Assert.True(UserAccessPolicy.IsBlocked(new UserProfile { IsActive = false }, "10.0.0.5", Noon));

    [Fact]
    public void Terminal_ip_restriction_blocks_other_addresses_but_not_loopback()
    {
        var profile = new UserProfile { AllowedIp = "10.0.0.5" };

        Assert.False(UserAccessPolicy.IsBlocked(profile, "10.0.0.5", Noon));
        Assert.True(UserAccessPolicy.IsBlocked(profile, "10.0.0.6", Noon));
        Assert.False(UserAccessPolicy.IsBlocked(profile, "127.0.0.1", Noon));
        Assert.False(UserAccessPolicy.IsBlocked(profile, "::1", Noon));
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(12, false)]
    [InlineData(17, false)]
    [InlineData(8, true)]
    [InlineData(18, true)]
    public void Operating_hours_window_is_enforced(int hour, bool blocked)
    {
        var profile = new UserProfile { WorkFrom = TimeSpan.FromHours(9), WorkTo = TimeSpan.FromHours(17).Add(TimeSpan.FromMinutes(30)) };

        Assert.Equal(blocked, UserAccessPolicy.IsBlocked(profile, null, TimeSpan.FromHours(hour)));
    }
}
