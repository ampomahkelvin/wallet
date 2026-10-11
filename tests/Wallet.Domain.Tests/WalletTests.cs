using FluentAssertions;
using Wallet.Domain;

namespace Wallet.Domain.Tests;

public class WalletTests
{
    [Fact]
    public void CreateUser_WithValidInput_ReturnsWallet()
    {
        var wallet = Wallet.CreateUser("user-1", "GHS");

        wallet.Id.Should().NotBeEmpty();
        wallet.Kind.Should().Be(WalletKind.User);
        wallet.OwnerId.Should().Be("user-1");
        wallet.Currency.Should().Be("GHS");
        wallet.Balance.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void CreateUser_WithInvalidOwnerId_Throws(string? ownerId)
    {
        Action act = () => Wallet.CreateUser(ownerId!, "GHS");
        act.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("GH")]
    [InlineData("GHSS")]
    [InlineData("gh")]
    public void CreateUser_WithInvalidCurrency_Throws(string currency)
    {
        Action act = () => Wallet.CreateUser("user-1", currency);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void CreateUser_NormalizesCurrencyToUpper()
    {
        var wallet = Wallet.CreateUser("user-1", "ghs");
        wallet.Currency.Should().Be("GHS");
    }

    [Fact]
    public void CreateSystem_WithValidInput_ReturnsWallet()
    {
        var wallet = Wallet.CreateSystem("GHS");

        wallet.Id.Should().NotBeEmpty();
        wallet.Kind.Should().Be(WalletKind.System);
        wallet.OwnerId.Should().BeNull();
        wallet.Currency.Should().Be("GHS");
        wallet.Balance.Should().Be(0);
    }

    [Fact]
    public void ApplyDelta_WhenUserWalletHasFunds_UpdatesBalance()
    {
        var wallet = Wallet.CreateUser("user-1", "GHS");
        wallet.ApplyDelta(100);
        wallet.Balance.Should().Be(100);
    }

    [Fact]
    public void ApplyDelta_WhenUserWalletGoesNegative_Throws()
    {
        var wallet = Wallet.CreateUser("user-1", "GHS");
        Action act = () => wallet.ApplyDelta(-1);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void ApplyDelta_WhenSystemWalletGoesNegative_Allows()
    {
        var wallet = Wallet.CreateSystem("GHS");
        wallet.ApplyDelta(-100);
        wallet.Balance.Should().Be(-100);
    }

    [Fact]
    public void ApplyDelta_WhenUserWalletBalanceSufficient_AllowsDebit()
    {
        var wallet = Wallet.CreateUser("user-1", "GHS");
        wallet.ApplyDelta(100);
        wallet.ApplyDelta(-50);
        wallet.Balance.Should().Be(50);
    }
}