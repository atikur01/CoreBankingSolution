using System.Diagnostics;
using CoreBankingSolution.Models;
using CoreBankingSolution.Models.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CoreBankingSolution.Controllers
{
    public class DashboardVm
    {
        public int Customers, ActiveAccounts, PendingVouchers, PendingAccounts, PendingLoans, PendingKyc, TodayTxns, CtrToday, NplLoans, RunningLoans;
        public decimal TotalDeposits, LoanOutstanding, TodayDebit, TodayCredit, Provision;
        public List<(string Name, decimal Amount)> DepositByProduct = [];
        public List<Branch> Branches = [];
        public List<Voucher> Recent = [];
    }

    public class HomeController : BankController
    {
        public async Task<IActionResult> Index()
        {
            var vm = new DashboardVm();
            var p = await Me.ProfileAsync();
            var branch = p?.Branch ?? await Db.Branches.FirstOrDefaultAsync();
            vm.Customers = await Db.Customers.CountAsync(c => c.IsActive);
            vm.ActiveAccounts = await Db.Accounts.CountAsync(a => a.Status == AccountStatus.Active);
            vm.PendingVouchers = await Db.Vouchers.CountAsync(v => v.Status == VoucherStatus.Pending);
            vm.PendingAccounts = await Db.Accounts.CountAsync(a => a.Status == AccountStatus.PendingApproval);
            vm.PendingLoans = await Db.Loans.CountAsync(l => l.Status == LoanStatus.Applied);
            vm.PendingKyc = await Db.Customers.CountAsync(c => c.Kyc == KycStatus.Pending);
            vm.TotalDeposits = await Db.Accounts.Where(a => a.Status != AccountStatus.Closed && a.Status != AccountStatus.PendingApproval).SumAsync(a => (decimal?)a.Balance) ?? 0;
            vm.LoanOutstanding = await Db.Loans.Where(l => l.Status == LoanStatus.Disbursed).SumAsync(l => (decimal?)l.OutstandingPrincipal) ?? 0;
            vm.RunningLoans = await Db.Loans.CountAsync(l => l.Status == LoanStatus.Disbursed);
            vm.NplLoans = await Db.Loans.CountAsync(l => l.Status == LoanStatus.Disbursed && l.Classification >= LoanClass.SubStandard);
            vm.Provision = await Db.Loans.SumAsync(l => (decimal?)l.ProvisionHeld) ?? 0;
            var d = branch?.BusinessDate ?? DateTime.Today;
            vm.TodayTxns = await Db.Vouchers.CountAsync(v => v.TxnDate == d && v.Status == VoucherStatus.Authorized);
            vm.TodayDebit = await Db.AccountTransactions.Where(t => t.TxnDate == d).SumAsync(t => (decimal?)t.Debit) ?? 0;
            vm.TodayCredit = await Db.AccountTransactions.Where(t => t.TxnDate == d).SumAsync(t => (decimal?)t.Credit) ?? 0;
            var ctr = await Svc<Services.ParamService>().DecAsync("CtrThreshold");
            vm.CtrToday = await Db.Vouchers.CountAsync(v => v.TxnDate == d && v.Status == VoucherStatus.Authorized && (v.Type == VoucherType.CashDeposit || v.Type == VoucherType.CashWithdrawal) && v.TotalAmount >= ctr);
            vm.DepositByProduct = (await Db.Accounts.Where(a => a.Status != AccountStatus.Closed && a.Status != AccountStatus.PendingApproval)
                .GroupBy(a => a.Product!.Name).Select(g => new { g.Key, S = g.Sum(x => x.Balance) }).ToListAsync()).Select(x => (x.Key, x.S)).ToList();
            vm.Branches = await Db.Branches.AsNoTracking().OrderBy(b => b.Code).ToListAsync();
            vm.Recent = await Db.Vouchers.AsNoTracking().OrderByDescending(v => v.Id).Take(8).ToListAsync();
            return View(vm);
        }

        [AllowAnonymous]
        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error() => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
