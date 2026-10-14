namespace Mercurius.Modules.Teams.Application.Services;

internal sealed class TeamInviteMaintenanceOptions
{
    public const string SectionName = "TeamInvite";
    public int RetentionDays { get; set; } = 90;
    public int MaintenanceBatchSize { get; set; } = 100;
    public int MaintenanceIntervalSeconds { get; set; } = 60;
    public int MaintenanceEventConcurrency { get; set; } = 4;

    public TimeSpan MaintenanceInterval => TimeSpan.FromSeconds(MaintenanceIntervalSeconds);
}
