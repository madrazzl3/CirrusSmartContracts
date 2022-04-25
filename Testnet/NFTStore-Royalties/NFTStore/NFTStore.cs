﻿using Stratis.SmartContracts;

[Deploy]
public class NFTStore : SmartContract //, INonFungibleTokenReceiver
{
    public SaleInfo GetSaleInfo(Address contract, UInt256 tokenId) => State.GetStruct<SaleInfo>($"SaleInfo:{contract}:{tokenId}");
    private void SetSaleInfo(Address contract, UInt256 tokenId, SaleInfo value) => State.SetStruct($"SaleInfo:{contract}:{tokenId}", value);
    private void ClearSaleInfo(Address contract, UInt256 tokenId) => State.Clear($"SaleInfo:{contract}:{tokenId}");

    private string GetSupportsRoyalty(Address contract) => State.GetString($"SupportsRoyalty:{contract}");
    private void SetSupportsRoyalty(Address contract, bool value) => State.SetString($"SupportsRoyalty:{contract}", value.ToString());

    public ulong CreatedAt
    {
        get => State.GetUInt64(nameof(CreatedAt));
        private set => State.SetUInt64(nameof(CreatedAt), value);
    }

    public ulong GetBalance(Address address)
    {
        return State.GetUInt64($"Balance:{address}");
    }

    private void SetBalance(Address address, ulong balance)
    {
        State.SetUInt64($"Balance:{address}", balance);
    }

    public NFTStore(ISmartContractState state) : base(state)
    {
        EnsureNotPayable();

        CreatedAt = Block.Number;
    }

    public void Buy(Address contract, UInt256 tokenId)
    {
        var saleInfo = GetSaleInfo(contract, tokenId);

        EnsureTokenIsOnSale(saleInfo);

        Assert(Message.Value == saleInfo.Price, "Transferred amount is not matching exact price of the token.");

        SafeTransferToken(contract, tokenId, Address, Message.Sender);

        ClearSaleInfo(contract, tokenId);

        var supportsRoyaltyInfo = SupportsRoyaltyInfo(contract);

        var royaltyInfo = supportsRoyaltyInfo
            ? GetRoyaltyInfo(contract, tokenId, saleInfo.Price)
            : new object[] { Address.Zero, 0UL };

        var royaltyRecipient = (Address)royaltyInfo[0];
        var royaltyAmount = (ulong)royaltyInfo[1];

        var salePriceMinusRoyalty = supportsRoyaltyInfo ? saleInfo.Price - royaltyAmount : saleInfo.Price;

        if (State.IsContract(saleInfo.Seller))
        {
            SetBalance(saleInfo.Seller, salePriceMinusRoyalty);
        }
        else
        {
            var result = Transfer(saleInfo.Seller, salePriceMinusRoyalty);

            Assert(result.Success, "Transfer failed.");
        }

        if (royaltyAmount > 0)
        {
            var royaltyTransfer = Transfer(royaltyRecipient, royaltyAmount);

            Assert(royaltyTransfer.Success, "Royalty transfer failed.");
            Log(new RoyaltyPaidLog { Recipient = royaltyRecipient, Amount = royaltyAmount });
        }

        Log(new TokenPurchasedLog { Contract = contract, TokenId = tokenId, Buyer = Message.Sender, Seller = saleInfo.Seller });
    }

    private object[] GetRoyaltyInfo(Address contract, UInt256 tokenId, ulong salePrice)
    {
        var royaltyCall = Call(contract, 0, "RoyaltyInfo", new object[] { tokenId, salePrice });

        Assert(royaltyCall.Success, "Get royalty info failed.");

        return royaltyCall.ReturnValue as object[];
    }

    public void CancelSale(Address contract, UInt256 tokenId)
    {
        EnsureNotPayable();
        var saleInfo = GetSaleInfo(contract, tokenId);

        EnsureTokenIsOnSale(saleInfo);

        EnsureCallerCanOperate(contract, saleInfo.Seller);

        SafeTransferToken(contract, tokenId, Address, saleInfo.Seller);

        ClearSaleInfo(contract, tokenId);

        Log(new TokenSaleCanceledLog { Contract = contract, TokenId = tokenId, Seller = saleInfo.Seller });
    }

    public bool OnNonFungibleTokenReceived(Address operatorAddress, Address fromAddress, UInt256 tokenId, byte[] data)
    {
        EnsureNotPayable();

        var seller = fromAddress == Address.Zero ? operatorAddress : fromAddress;

        var price = Serializer.ToUInt64(data);

        Assert(price > 0, "Price should be higher than zero.");

        var tokenContract = Message.Sender;

        Assert(State.IsContract(tokenContract), "The Caller is not a contract.");

        Assert(Address == GetOwner(tokenContract, tokenId), "The store contract is not owner of the token.");

        var saleInfo = GetSaleInfo(tokenContract, tokenId);

        Assert(saleInfo.Price == 0, "The token is already on sale.");

        SetSaleInfo(tokenContract, tokenId, new SaleInfo { Price = price, Seller = seller });

        Log(new TokenOnSaleLog { Contract = tokenContract, TokenId = tokenId, Price = price, Seller = seller, Operator = operatorAddress });

        return true;
    }

    public bool Withdraw()
    {
        EnsureNotPayable();

        var amount = GetBalance(Message.Sender);

        Assert(amount > 0);

        SetBalance(Message.Sender, 0);

        var transfer = Transfer(Message.Sender, amount);

        Assert(transfer.Success, "Transfer failed.");

        Log(new BalanceRefundedLog { To = Message.Sender, Amount = amount });

        return transfer.Success;
    }

    private bool IsApprovedForAll(Address contract, Address tokenOwner)
    {
        var result = Call(contract, 0, "IsApprovedForAll", new object[] { tokenOwner, Message.Sender });

        Assert(result.Success, "IsApprovedForAll method call failed.");

        return result.ReturnValue is bool success && success;
    }

    private void SafeTransferToken(Address contract, UInt256 tokenId, Address from, Address to)
    {
        var result = Call(contract, 0, "SafeTransferFrom", new object[] { from, to, tokenId });

        Assert(result.Success, "The token transfer failed.");
    }

    private Address GetOwner(Address contract, UInt256 tokenId)
    {
        var result = Call(contract, 0, "OwnerOf", new object[] { tokenId });

        Assert(result.Success && result.ReturnValue is Address, "OwnerOf method call failed.");

        return (Address)result.ReturnValue;
    }

    private void EnsureTokenIsOnSale(SaleInfo saleInfo)
    {
        Assert(saleInfo.Price > 0, "The token is not on sale.");
    }

    private void EnsureCallerCanOperate(Address contract, Address tokenOwner)
    {
        var isOwnerOrOperator = Message.Sender == tokenOwner || IsApprovedForAll(contract, tokenOwner);

        Assert(isOwnerOrOperator, "The caller is not owner of the token nor approved for all.");
    }

    private void EnsureNotPayable() => Assert(Message.Value == 0, "The method is not payable.");

    private bool SupportsRoyaltyInfo(Address contract)
    {
        var cached = GetSupportsRoyalty(contract);

        if (cached != null)
            return cached == bool.TrueString;

        var result = Call(contract, 0, "SupportsInterface", new object[] { 6 /* IRoyaltyInfo */ });

        var supported = result.Success && result.ReturnValue is bool value && value;

        SetSupportsRoyalty(contract, supported);

        return supported;
    }

    public struct TokenOnSaleLog
    {
        [Index]
        public Address Contract;
        [Index]
        public Address Seller;
        [Index]
        public Address Operator;
        [Index]
        public UInt256 TokenId;
        public ulong Price;

    }

    public struct TokenSaleCanceledLog
    {
        [Index]
        public Address Contract;
        [Index]
        public UInt256 TokenId;
        [Index]
        public Address Seller;
    }

    public struct TokenPurchasedLog
    {
        [Index]
        public Address Contract;
        public UInt256 TokenId;
        [Index]
        public Address Buyer;
        [Index]
        public Address Seller;
    }

    public struct RoyaltyPaidLog
    {
        [Index]
        public Address Recipient;
        public ulong Amount;
    }

    public struct BalanceRefundedLog
    {
        [Index]
        public Address To;
        public ulong Amount;
    }

    public struct SaleInfo
    {
        public ulong Price;
        public Address Seller;
    }

    public struct ReceiveInfo
    {
        public Address Contract;
        public ulong Price;
    }
}
