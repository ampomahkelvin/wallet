using FluentAssertions;
using Wallet.Domain;

namespace Wallet.Domain.Tests;

public class TransferTests
{
    [Fact]
    public void Create_WithValidInput_ReturnsTransferWithTwoEntries()
    {
        var sourceId = Guid.NewGuid();
        var destId = Guid.NewGuid();

        var transfer = Transfer.Create(TransferType.Transfer, sourceId, destId, "GHS", 100);

        transfer.Id.Should().NotBeEmpty();
        transfer.Type.Should().Be(TransferType.Transfer);
        transfer.SourceWalletId.Should().Be(sourceId);
        transfer.DestinationWalletId.Should().Be(destId);
        transfer.Currency.Should().Be("GHS");
        transfer.Amount.Should().Be(100);
        transfer.Entries.Should().HaveCount(2);
        transfer.Entries.Sum(e => e.Amount).Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void Create_WithNonPositiveAmount_Throws(long amount)
    {
        var sourceId = Guid.NewGuid();
        var destId = Guid.NewGuid();

        Action act = () => Transfer.Create(TransferType.Transfer, sourceId, destId, "GHS", amount);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Create_WithSameSourceAndDest_Throws()
    {
        var walletId = Guid.NewGuid();

        Action act = () => Transfer.Create(TransferType.Transfer, walletId, walletId, "GHS", 100);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Create_ReversalWithoutReversesTransferId_Throws()
    {
        var sourceId = Guid.NewGuid();
        var destId = Guid.NewGuid();

        Action act = () => Transfer.Create(TransferType.Reversal, sourceId, destId, "GHS", 100);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Create_NonReversalWithReversesTransferId_Throws()
    {
        var sourceId = Guid.NewGuid();
        var destId = Guid.NewGuid();
        var reversalId = Guid.NewGuid();

        Action act = () => Transfer.Create(TransferType.Transfer, sourceId, destId, "GHS", 100, reversalId);
        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Create_WithValidReversal_ReturnsReversalTransfer()
    {
        var sourceId = Guid.NewGuid();
        var destId = Guid.NewGuid();
        var originalId = Guid.NewGuid();

        var transfer = Transfer.Create(TransferType.Reversal, sourceId, destId, "GHS", 100, originalId);

        transfer.Type.Should().Be(TransferType.Reversal);
        transfer.ReversesTransferId.Should().Be(originalId);
    }

    [Fact]
    public void Create_ReturnsEntriesWithCorrectSignedAmounts()
    {
        var sourceId = Guid.NewGuid();
        var destId = Guid.NewGuid();

        var transfer = Transfer.Create(TransferType.Transfer, sourceId, destId, "GHS", 100);

        var sourceEntry = transfer.Entries.First(e => e.WalletId == sourceId);
        var destEntry = transfer.Entries.First(e => e.WalletId == destId);

        sourceEntry.Amount.Should().Be(-100);
        destEntry.Amount.Should().Be(100);
    }

    [Fact]
    public void SetEntryBalances_UpdatesBalanceAfterValues()
    {
        var sourceId = Guid.NewGuid();
        var destId = Guid.NewGuid();

        var transfer = Transfer.Create(TransferType.Transfer, sourceId, destId, "GHS", 100);
        transfer.SetEntryBalances(0, 200);

        var sourceEntry = transfer.Entries.First(e => e.WalletId == sourceId);
        var destEntry = transfer.Entries.First(e => e.WalletId == destId);

        sourceEntry.BalanceAfter.Should().Be(0);
        destEntry.BalanceAfter.Should().Be(200);
    }
}