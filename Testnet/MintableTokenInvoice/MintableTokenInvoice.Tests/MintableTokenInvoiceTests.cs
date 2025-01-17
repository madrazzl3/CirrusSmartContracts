namespace MintableTokenInvoiceTests;

using FluentAssertions;
using Moq;
using NBitcoin.DataEncoders;
using Newtonsoft.Json;
using Stratis.SmartContracts;
using Stratis.SmartContracts.CLR;
using Xunit;

// Claim as defined by Identity Server.
public class Claim
{
    public Claim()
    {
    }

    public string Key { get; set; }

    public string Description { get; set; }

    public bool IsRevoked { get; set; }
}

public struct Invoice
{
    public string Symbol;
    public UInt256 Amount;
    public Address To;
    public string Outcome;
    public bool IsAuthorized;
}

/// <summary>
/// These tests validate the functionality that differs between the original standard token and the extended version.
/// </summary>
public class MintableTokenInvoiceTests : BaseContractTest
{
    [Fact]
    public void Constructor_Assigns_Owner()
    {
        var mintableTokenInvoice = this.CreateNewMintableTokenContract();

        // Verify that PersistentState was called with the contract owner
        mintableTokenInvoice.Owner.Should().Be(this.Owner);
    }

    [Fact]
    public void TransferOwnership_Succeeds_For_Owner()
    {
        var mintableTokenInvoice = this.CreateNewMintableTokenContract();
        mintableTokenInvoice.SetNewOwner(this.AddressOne);
        this.SetupMessage(this.Contract, this.AddressOne);
        mintableTokenInvoice.ClaimOwnership();
        mintableTokenInvoice.Owner.Should().Be(this.AddressOne);
    }

    [Fact]
    public void TransferOwnership_Fails_For_NonOwner()
    {
        var mintableTokenInvoice = this.CreateNewMintableTokenContract();
        this.SetupMessage(this.Contract, this.AddressOne);
        Assert.ThrowsAny<SmartContractAssertException>(() => mintableTokenInvoice.SetNewOwner(this.AddressTwo));
    }

    [Fact]
    public void CanDeserializeClaim()
    {
        // First serialize the claim data as the IdentityServer would do it when calling "AddClaim".
        var claim = new Claim() { Key = "Identity Approved", Description = "Identity Approved", IsRevoked = false };

        var bytes = new ASCIIEncoder().DecodeData(JsonConvert.SerializeObject(claim));

        var json = this.Serializer.ToString(bytes);

        Assert.Contains("Identity Approved", json);
    }

    [Fact]
    public void CanCreateInvoice()
    {
        var mintableTokenInvoice = this.CreateNewMintableTokenContract();

        UInt128 uniqueNumber = 1;

        this.SetupMessage(this.Contract, this.AddressOne);

        var claim = new Claim() { Description = "Identity Approved", IsRevoked = false, Key = "Identity Approved" };
        var claimBytes = new ASCIIEncoder().DecodeData(JsonConvert.SerializeObject(claim));

        this.MockInternalExecutor
            .Setup(x => x.Call(It.IsAny<ISmartContractState>(), It.IsAny<Address>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<ulong>()))
            .Returns((ISmartContractState state, Address address, ulong amount, string methodName, object[] args, ulong gasLimit) =>
            {
                return TransferResult.Transferred(claimBytes);
            });

        var transactionReference = mintableTokenInvoice.CreateInvoice("GBPT", 100, uniqueNumber);
        var invoiceReference = mintableTokenInvoice.GetInvoiceReference(transactionReference);

        Assert.Equal("INV-1760-4750-2039", invoiceReference.ToString());

        // 42 is checksum for INV numbers.
        Assert.Equal(42UL, ulong.Parse(invoiceReference.Replace("-", string.Empty)[3..]) % 97);

        Assert.Equal("REF-5377-4902-2339", transactionReference.ToString());

        // 1 is checksum for REF numbers.
        Assert.Equal(1UL, ulong.Parse(transactionReference.Replace("-", string.Empty)[3..]) % 97);

        var invoiceBytes = mintableTokenInvoice.RetrieveInvoice(invoiceReference, true);
        var invoice = this.Serializer.ToStruct<Invoice>(invoiceBytes);

        Assert.Equal(100, invoice.Amount);
        Assert.Equal("GBPT", invoice.Symbol);
        Assert.Equal(this.AddressOne, invoice.To);
        Assert.True(invoice.IsAuthorized);
        Assert.Null(invoice.Outcome);
    }


    [Fact]
    public void CantCreateInvoiceIfNotKYCed()
    {
        var mintableTokenInvoice = this.CreateNewMintableTokenContract();

        UInt128 uniqueNumber = 1;

        this.SetupMessage(this.Contract, this.AddressOne);

        this.MockInternalExecutor
            .Setup(x => x.Call(It.IsAny<ISmartContractState>(), It.IsAny<Address>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<ulong>()))
            .Returns((ISmartContractState state, Address address, ulong amount, string methodName, object[] args, ulong gasLimit) =>
            {
                return null;
            });

        var ex = Assert.Throws<SmartContractAssertException>(() => mintableTokenInvoice.CreateInvoice("GBPT", 100, uniqueNumber));
        Assert.Contains("verification", ex.Message);
    }

    [Fact]
    public void CantCreateInvoiceIfNotAuthorized()
    {
        var mintableTokenInvoice = this.CreateNewMintableTokenContract();

        UInt128 uniqueNumber = 1;

        this.SetupMessage(this.Contract, this.AddressOne);

        var claim = new Claim() { Description = "Identity Approved", IsRevoked = false, Key = "Identity Approved" };
        var claimBytes = new ASCIIEncoder().DecodeData(JsonConvert.SerializeObject(claim));

        this.MockInternalExecutor
            .Setup(x => x.Call(It.IsAny<ISmartContractState>(), It.IsAny<Address>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<ulong>()))
            .Returns((ISmartContractState state, Address address, ulong amount, string methodName, object[] args, ulong gasLimit) =>
            {
                return TransferResult.Transferred(claimBytes);
            });

        var ex = Assert.Throws<SmartContractAssertException>(() => mintableTokenInvoice.CreateInvoice("GBPT", 2000, uniqueNumber));
        Assert.Contains("authorization", ex.Message);
    }

    [Fact]
    public void CantCreateInvoiceIfDidNotExist()
    {
        var mintableTokenInvoice = this.CreateNewMintableTokenContract();

        UInt128 uniqueNumber = 1;

        var claim = new Claim() { Description = "Identity Approved", IsRevoked = false, Key = "Identity Approved" };
        var claimBytes = new ASCIIEncoder().DecodeData(JsonConvert.SerializeObject(claim));

        this.MockInternalExecutor
            .Setup(x => x.Call(It.IsAny<ISmartContractState>(), It.IsAny<Address>(), It.IsAny<ulong>(), It.IsAny<string>(), It.IsAny<object[]>(), It.IsAny<ulong>()))
            .Returns((ISmartContractState state, Address address, ulong amount, string methodName, object[] args, ulong gasLimit) =>
            {
                return TransferResult.Transferred(claimBytes);
            });


        // The minters will set this status for any payment reference that could not be processed.
        // We don't want to process these payments at a later stage as they may get refunded.
        mintableTokenInvoice.SetOutcome("REF-5377-4902-2339", "Payment could not be processed");

        this.SetupMessage(this.Contract, this.AddressOne);

        // Check that we don't "create" an invoice for a payment reference associated with an existing outcome.
        var ex = Assert.Throws<SmartContractAssertException>(() => mintableTokenInvoice.CreateInvoice("GBPT", 200, uniqueNumber));
        Assert.Contains("processed", ex.Message);
    }

    [Fact]
    public void CanSetIdentityContract()
    {
        var mintableTokenInvoice = this.CreateNewMintableTokenContract();

        mintableTokenInvoice.SetIdentityContract(this.Contract);

        Assert.Equal(this.Contract, mintableTokenInvoice.IdentityContract);
    }

    [Fact]
    public void CanSetKYCProvider()
    {
        var mintableTokenInvoice = this.CreateNewMintableTokenContract();

        mintableTokenInvoice.SetKYCProvider(2);

        Assert.Equal((uint)2, mintableTokenInvoice.KYCProvider);
    }

    [Fact]
    public void CanSetAuthorizationLimit()
    {
        var mintableTokenInvoice = this.CreateNewMintableTokenContract();

        mintableTokenInvoice.SetAuthorizationLimit(300);

        Assert.Equal((UInt256)300, mintableTokenInvoice.AuthorizationLimit);
    }
}