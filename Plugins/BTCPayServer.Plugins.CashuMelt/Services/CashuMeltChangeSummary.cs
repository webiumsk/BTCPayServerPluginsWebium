namespace BTCPayServer.Plugins.CashuMelt.Services;

public sealed record CashuMeltChangeSummary(
    int Version, string PendingSat, string SweptSat, string MinimumSweepSat,
    int UnresolvedRecords, int AvailableRecords, int ExportedRecords, int SweepingRecords,
    int ManualReviewRecords, int UnsupportedUnitRecords, int OtherUnresolvedRecords);
