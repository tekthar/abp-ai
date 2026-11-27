using Microsoft.EntityFrameworkCore;

namespace AbpAi.Host.Tests;

/// <summary>0.10.0's: a scope the host purges is forgotten (<see cref="AiAdminService.ForgetScopeAsync"/>), its calls kept on the ledger.</summary>
public sealed partial class GatewayHostTests
{
    [Fact]
    public async Task A_purged_scope_is_forgotten_and_its_calls_stay_on_the_ledger_without_their_text()
    {
        AiAdminService admin = Get<AiAdminService>();
        AiExportService exports = Get<AiExportService>();
        await admin.SetSiteAsync(SiteAdmin, new AiSiteSettings(true, true, 30, [AiDataClasses.Public, AiDataClasses.Internal], []), default);

        // Payroll has a connection of its own, an alias, a limit, its own access, a kept call and an export of its own.
        AiConnectionInfo own = await admin.CreateConnectionAsync(FinanceAdmin, Payroll,
            new AiConnectionInput("payroll-cloud", "demo", ["pay-model"], [AiDataClasses.Internal]) { ApiKey = "sk-payroll", OutputPrice = 1000 }, default);
        await admin.SetAliasAsync(FinanceAdmin, Payroll, "smart", [new AiAliasTarget(own.Id)], default);
        await admin.SetLimitAsync(FinanceAdmin, Payroll, new AiLimitInput(AiLimitKinds.Total, AiPeriods.Month, 5m), default);
        await admin.SetScopeAccessAsync(SiteAdmin, Payroll, new AiScopeAccessInput(true, null, true), default);
        // Its department and its sibling have their own, which stay.
        await admin.SetLimitAsync(FinanceAdmin, Finance, new AiLimitInput(AiLimitKinds.Total, AiPeriods.Month, 5m), default);
        await admin.SetAliasAsync(FinanceAdmin, Budget, "smart", [new AiAliasTarget(_onPrem)], default);

        AiReply inPayroll = await Ask(Payroll, Ben, dataClass: AiDataClasses.Internal);
        Assert.Equal("payroll-cloud", inPayroll.Connection);
        AiReply inBudget = await Ask(Budget, Ben);
        DateTime day = DateTime.UtcNow.Date;
        AiExportInfo payrolls = await exports.CreateAsync(SiteAdmin, day, day.AddDays(1), Payroll, default);
        AiExportInfo sites = await exports.CreateAsync(SiteAdmin, day, day.AddDays(1), null, default);

        Assert.Equal(new AiScopeForgotten(Payroll, Connections: 1, Aliases: 1, Limits: 1, Access: true, Prompts: 1, Exports: 1),
            await admin.ForgetScopeAsync(Payroll, default));

        Assert.False(await Db(db => db.AiConnections.AnyAsync(c => c.Id == own.Id)));
        Assert.False(await Db(db => db.AiAliases.AnyAsync(a => a.ScopeId == Payroll)));
        Assert.False(await Db(db => db.AiLimits.AnyAsync(l => l.ScopeId == Payroll)));
        Assert.False(await Db(db => db.AiScopeAccess.AnyAsync(a => a.Id == Payroll)));
        Assert.False(await Db(db => db.AiPrompts.AnyAsync(p => p.Id == inPayroll.UsageId)));
        Assert.False(await Db(db => db.AiExports.AnyAsync(e => e.Id == payrolls.Id)));

        // Its call stays on the ledger, under its department too, so the department's limit still counts it.
        AiUsageRecord call = await Db(db => db.AiUsage.SingleAsync(u => u.Id == inPayroll.UsageId));
        Assert.Equal((Payroll, AiUsageStates.Done), (call.ScopeId, call.State));
        Assert.Contains(Finance, call.ScopePath);
        Assert.True(call.Cost > 0);

        // What isn't Payroll's stays: its sibling's alias and kept call, its department's limit, the site's export.
        Assert.True(await Db(db => db.AiAliases.AnyAsync(a => a.ScopeId == Budget)));
        Assert.True(await Db(db => db.AiLimits.AnyAsync(l => l.ScopeId == Finance)));
        Assert.True(await Db(db => db.AiPrompts.AnyAsync(p => p.Id == inBudget.UsageId)));
        Assert.True(await Db(db => db.AiExports.AnyAsync(e => e.Id == sites.Id)));
        Assert.Equal("public-cloud", (await Get<IAi>().ResolveAsync("smart", Pipeline, AiDataClasses.Public)).Connection);

        // A second call finds nothing; a scope is named.
        Assert.Equal(new AiScopeForgotten(Payroll, 0, 0, 0, false, 0, 0), await admin.ForgetScopeAsync(Payroll, default));
        await Assert.ThrowsAsync<ArgumentException>(() => admin.ForgetScopeAsync(Guid.Empty, default));
    }
}
