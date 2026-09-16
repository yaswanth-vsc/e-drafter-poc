using System.Collections.Concurrent;

namespace EDrafter.MockServer;

/// <summary>
/// In-memory stand-in for eDrafter's data. Everything the mock knows lives here.
/// Deliberately not persisted: restarting the mock is how you reset a test run.
/// </summary>
public sealed class MockState
{
    public const string KarnatakaProductId = "664a1b2c3d4e5f6a7b8c9d02";

    // Seeded from the clock rather than a constant: a restart would otherwise reissue
    // the same ids, and any client that persists them (ours does) would match a new
    // order against a stale record. Real eDrafter ids are never reused.
    private int _nextOrderIdd = 100_000 + (int)(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond % 800_000);
    private int _nextDisplayNo = 1;

    public decimal WalletBalance { get; set; } = 5_000m;

    public ConcurrentDictionary<int, MockOrder> Orders { get; } = new();
    public ConcurrentDictionary<string, MockEsignDoc> EsignDocs { get; } = new();
    public ConcurrentDictionary<string, MockWebhook> Webhooks { get; } = new();
    public ConcurrentBag<MockTransaction> Transactions { get; } = new();

    public int NextOrderIdd() => Interlocked.Increment(ref _nextOrderIdd);
    public string NextDisplayId() => $"MSB{Interlocked.Increment(ref _nextDisplayNo):D3}";

    /// <summary>
    /// Debits the wallet and records a ledger entry, mirroring eDrafter's behaviour.
    /// Returns false when the balance will not cover it, so the caller can 402.
    /// </summary>
    public bool TryDebit(decimal amount, string description)
    {
        lock (_walletLock)
        {
            if (WalletBalance < amount) return false;
            WalletBalance -= amount;
            Transactions.Add(new MockTransaction("Debit", amount, description, DateTime.UtcNow));
            return true;
        }
    }

    public void Credit(decimal amount, string description)
    {
        lock (_walletLock)
        {
            WalletBalance += amount;
            Transactions.Add(new MockTransaction("Credit", amount, description, DateTime.UtcNow));
        }
    }

    private readonly object _walletLock = new();
}

public sealed class MockOrder
{
    public int Idd { get; init; }
    public string DisplayId { get; init; } = "";
    public string? RefId { get; init; }
    public string Status { get; set; } = "Pending";
    public string FirstParty { get; init; } = "";
    public string SecondParty { get; init; } = "";
    public string? ArticleCode { get; init; }
    public string? Purpose { get; init; }
    public int Quantity { get; init; }
    public decimal Denomination { get; init; }
    public decimal? ConsiderationPrice { get; init; }
    public decimal TotalAmount { get; init; }
    public bool DoorstepDelivery { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public List<MockStamp> Stamps { get; } = new();
}

public sealed class MockStamp
{
    public int StampId { get; init; }
    public string CertificateNo { get; init; } = "";
    public decimal Denomination { get; init; }
    public string State { get; init; } = "Karnataka";
    public bool IsConsumed { get; set; }
    public bool Downloaded { get; set; }
}

public sealed class MockEsignDoc
{
    public string DocumentId { get; init; } = "";
    public string Uid { get; init; } = "";
    public string Name { get; init; } = "";
    public string Status { get; set; } = "sent";
    public string SignMethod { get; init; } = "";
    public decimal ChargedAmount { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; init; }
    public List<MockSignatory> Signatories { get; } = new();
    public string? SignedUrl { get; set; }
}

public sealed class MockSignatory
{
    public string Name { get; init; } = "";
    public string Email { get; init; } = "";
    public string? Phone { get; init; }
    public string Status { get; set; } = "pending";
    public DateTime? SignedAt { get; set; }
    public string SignUrl { get; init; } = "";
}

public sealed class MockWebhook
{
    public string Id { get; init; } = "";
    public string Url { get; init; } = "";
    public List<string> Events { get; init; } = new();
    public string Secret { get; init; } = "";
    public bool Active { get; set; } = true;
}

public sealed record MockTransaction(string Type, decimal Amount, string Description, DateTime At);
