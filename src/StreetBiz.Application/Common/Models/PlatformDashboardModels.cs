namespace StreetBiz.Application.Common.Models;

/// <summary>Everything ADM-06 counts inside one period (Vietnamese calendar days, inclusive).</summary>
public sealed record DashboardPeriodRow(
    int NewUsers,
    int OrdersPlaced,
    int OrdersCompleted,
    int OrdersCancelled,
    int OrdersRejected,
    decimal Gmv,
    int ReportsFiled,
    int ReportsReviewed,
    double? AverageReviewHours);

/// <summary>Counts that describe the platform right now, whatever period is on screen.</summary>
public sealed record DashboardSnapshotRow(
    int UsersTotal,
    int Customers,
    int Vendors,
    int WardAuthorities,
    int PlatformAdmins,
    int SuspendedUsers,
    int StorefrontsOpen,
    int StorefrontsPaused,
    int StorefrontsClosed,
    int StorefrontsHidden,
    int PendingReports,
    int OpenComplaints,
    int PendingRefunds,
    decimal PendingRefundAmount);

public sealed record DashboardOrderStatusRow(string Status, int Count);

public sealed record DashboardDayRow(DateOnly Day, int OrdersPlaced, int OrdersCompleted, decimal Gmv);

public sealed record DashboardTopStorefrontRow(
    long StorefrontId,
    string StorefrontName,
    int CompletedOrders,
    decimal Revenue);

public sealed record PlatformDashboardFacts(
    DashboardPeriodRow Current,
    DashboardPeriodRow Previous,
    DashboardSnapshotRow Snapshot,
    IReadOnlyList<DashboardOrderStatusRow> OrdersByStatus,
    IReadOnlyList<DashboardDayRow> Days,
    IReadOnlyList<DashboardTopStorefrontRow> TopStorefronts);

/// <summary>The period on screen and the equally long one just before it, as Vietnamese calendar days.</summary>
public sealed record DashboardRange(
    DateOnly From,
    DateOnly To,
    DateOnly PreviousFrom,
    DateOnly PreviousTo);
