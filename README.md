# Core Banking Solution (CBS)

ASP.NET Core 9 MVC + Entity Framework Core + SQL Server + ASP.NET Identity.
Built against the Bangladesh Bank guideline **"Guidelines on Core Banking Solution (CBS) Features and Controls", Version 2.0 (BRPD Circular No. 12, 13 May 2024)** – see `bb bank software.pdf`.

## Run

```bash
dotnet run
```

* The connection string is read from `appsettings.json` → `ConnectionStrings:DefaultConnection`.
* On start-up the app applies the EF migrations (`Database:Mode` = `Migrate`) and seeds reference data: currencies, branches (001 Head Office, 002 Motijheel), chart of accounts, products, bank parameters, excise slabs and the roles.
* First login: **`admin@corebank.local` / `Admin@12345`** – you are forced to change the password immediately.
* Public registration is disabled; administrators create users under *Administration → Users* (a one-time temporary password is shown).

## Module map (guideline section → implementation)

| BB guideline | Implemented |
|---|---|
| 1.1 Bank & branch setup | Branches with layers (HO>Division>Region>Cluster>Branch>Sub-branch>Booth), cost/profit centres, routing no., 13-digit account numbers with Luhn check digit (branch 3 + product 2 + serial 7 + check 1), CIF with check digit, configurable products & fee schedule, maker-checker approved back-dated interest-rate slabs, holiday calendar, bank parameters |
| 1.2 EOD/BOD, month-end, year-end | Branch-wise EOD in one transaction with step log; blocks on unauthorized vouchers; standing instructions, interest accrual, overdraft interest, loan interest recognition, classification & provisioning, FDR/DPS maturity/auto-renewal, periodic interest payment with source tax, dormant/inoperative flagging, year-end excise duty and P&L → retained earnings transfer; BOD opens next working day |
| 1.3 General ledger | Tree chart of accounts, double-entry vouchers, self-balancing check, per-branch balancing via inter-branch GL, back-value entries (Manager level, max days), reversal vouchers (all related lines), maker-checker, 2 authorization levels, day-close block, VAT/tax/excise parameters, no edit after authorization |
| 1.4 Reconciliation | Inter-branch reconciliation report (must net to zero), clearing/settlement GL |
| 1.5 Clearing | Inward/outward clearing register, settlement vouchers, returns with cheque-return register and dishonour charge |
| 1.6–1.8 Accounts, teller, SI | Cash deposit/withdrawal/transfer (same & inter-branch), limits (single/daily/monthly), lien, cheque books, stop payment, returned cheques, standing instructions |
| 2 Bills & remittance | Payment order, demand draft, TT: issue, pay, cancel, stop, commission + VAT |
| 3 Deposits | Savings, Current (with overdraft), SND, FDR (premature/maturity/auto-renew), DPS, joint accounts, nominees, power of attorney, KYC, all 20+ customer categories |
| 4 Loans | Term (EMI) and demand loans, application → approval → disbursement → repayment, schedules, installment allocation, overdue days, SMA/SS/DF/BL classification, provisioning |
| 8 CRM | Customer master (CIF), photo/signature/NID upload, KYC verify (maker-checker), risk rating, PEP flag |
| 9 / UNSCR | UNSCR + local sanction list screening on customers, joint holders, nominees, PoA, introducer, depositor/withdrawer, transfer beneficiary, loans, remittance |
| 10 System administration | Users with branch/employee ID/prime menu/IP/operating hours/limits, roles (Administrator, Manager, Supervisor, Officer, Teller), password policy (8+ chars complexity, history of 4, 30-day expiry, forced change, lockout), 2FA via authenticator app, full audit trail |
| 11 Reporting | Trial balance, GL statement, day book, balance sheet, income statement, inter-branch reconciliation, deposit portfolio, dormant accounts, FDR/DPS maturity, loan portfolio & provision, unauthorized transactions, CTR, cheque registers, user activity, account statement, audit trail – all printable and exportable to CSV |

**Not implemented (out of scope):** Trade Service (LC/export/import, section 5), Treasury front/middle/back office (section 6), alternative-delivery-channel integration such as ISO 8583/ATM/POS (section 7), multi-currency revaluation, Islamic-bank profit-sharing calculation. Multi-currency exists only as master data (currencies/spot rates).

## Key controls

* **Maker-checker** – the creator of a voucher, KYC, loan or rate can never approve it; amounts ≥ `Level2Amount` and back-valued entries need Manager-level approval.
* **Audit trail** – every insert/update/delete is logged (user, IP, before→after values).
* **Day close** – blocked while unauthorized vouchers, unapproved accounts or unsettled clearing items remain.
* Configure GL codes, rates, tax/VAT, thresholds under *Setup → Bank parameters*.
