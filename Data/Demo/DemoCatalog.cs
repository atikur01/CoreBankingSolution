namespace CoreBankingSolution.Data.Demo;

internal static class DemoCatalog
{
    public record Person(string Name, string Father, string Mother, string Gender, string Profession, decimal Income);

    public record Corporate(string Name, Models.Entities.CustomerType Type, string Contact, string Sector, string License);

    public record DemoUser(string Email, string FullName, string Role, string BranchCode, string Designation, string EmployeeId, decimal MaxCredit, decimal MaxDebit);

    public static readonly Person[] People =
    [
        new("Md. Rafiqul Islam", "Abdul Karim", "Rahima Khatun", "Male", "Businessman", 180000),
        new("Fatema Begum", "Md. Abul Hossain", "Jahanara Begum", "Female", "Teacher", 55000),
        new("Mohammad Tanvir Ahmed", "Kamal Uddin Ahmed", "Nasima Akter", "Male", "Software Engineer", 120000),
        new("Nusrat Jahan", "Md. Shahidul Alam", "Rokeya Sultana", "Female", "Doctor", 150000),
        new("Abdul Mannan Sarker", "Late Abdus Salam", "Amena Begum", "Male", "Retired Government Officer", 45000),
        new("Sharmin Akter", "Md. Jamal Uddin", "Salma Khatun", "Female", "Banker", 85000),
        new("Md. Kamrul Hasan", "Md. Nurul Haque", "Morzina Begum", "Male", "Garments Merchandiser", 70000),
        new("Tahmina Rahman", "Mizanur Rahman", "Shirin Rahman", "Female", "University Lecturer", 95000),
        new("Shafiqul Alam Chowdhury", "Late Nazrul Chowdhury", "Ayesha Chowdhury", "Male", "Importer", 350000),
        new("Rokeya Sultana", "Md. Anwar Hossain", "Minara Begum", "Female", "Homemaker", 20000),
        new("Mahmudul Hasan Talukder", "Late Abdur Rob Talukder", "Hosne Ara", "Male", "Member of Parliament", 400000),
        new("Israt Jahan Mim", "Md. Delwar Hossain", "Parvin Akter", "Female", "Fashion Designer", 65000),
        new("Md. Sohel Rana", "Md. Abdus Sattar", "Rina Begum", "Male", "Shopkeeper", 60000),
        new("Anwara Khatun", "Late Md. Ismail", "Fazilatunnesa", "Female", "Retired Teacher", 30000),
        new("Zahid Hasan Emon", "Md. Mozammel Haque", "Rehana Parvin", "Male", "Civil Engineer", 110000),
        new("Sabina Yasmin", "Md. Rafiqul Alam", "Nurjahan Begum", "Female", "Nurse", 40000),
        new("Md. Habibur Rahman", "Abdur Razzak", "Majeda Khatun", "Male", "Agro Trader", 50000),
        new("Lutfun Nahar", "Md. Lutfor Rahman", "Sufia Khatun", "Female", "Boutique Owner", 75000),
        new("Arif Hossain Bhuiyan", "Md. Shamsul Haque Bhuiyan", "Kohinoor Begum", "Male", "Pharmacist", 60000),
        new("Mst. Shapla Khatun", "Md. Abdul Latif", "Bilkis Begum", "Female", "Tailor", 25000),
        new("Sajjad Hossain Khan", "Md. Akbar Hossain Khan", "Rabeya Khan", "Male", "Chartered Accountant", 160000),
        new("Dilruba Haque", "Md. Ziaul Haque", "Monowara Begum", "Female", "Journalist", 70000),
        new("Md. Jahangir Alam", "Md. Abdul Khaleque", "Taslima Begum", "Male", "Transport Operator", 90000),
        new("Mehnaz Chowdhury", "Rafiq Chowdhury", "Salina Chowdhury", "Female", "Architect", 130000),
        new("Md. Ashraful Islam", "Md. Nazrul Islam", "Jorina Begum", "Male", "Insurance Agent", 55000),
        new("Taslima Nasrin Ripa", "Md. Abul Kalam", "Nazma Begum", "Female", "Freelancer", 45000),
        new("Md. Golam Mostafa", "Md. Abdul Wahab", "Mamataz Begum", "Male", "Rice Mill Owner", 280000),
        new("Farhana Yeasmin", "Md. Yeasin Ali", "Rehana Begum", "Female", "Dentist", 140000),
    ];

    public static readonly Corporate[] Corporates =
    [
        new("Padma Textiles Ltd.", Models.Entities.CustomerType.PrivateLtd, "Mr. Anisur Rahman", "Textile Manufacturing", "TRAD/DSCC/045678/2022"),
        new("Meghna Agro Traders", Models.Entities.CustomerType.Proprietary, "Md. Fazlul Karim", "Agro Trading", "TRAD/DNCC/118203/2023"),
        new("Shonar Bangla Foundation", Models.Entities.CustomerType.NonProfitOrganization, "Ms. Rehana Chowdhury", "Education & Welfare", "NGOAB/2019/3321"),
        new("Bengal Pharma PLC", Models.Entities.CustomerType.PublicLtd, "Dr. Selim Reza", "Pharmaceuticals", "C-93017/2012"),
    ];

    public static readonly DemoUser[] Users =
    [
        new("manager@corebank.local", "Kamal Hossain", "Manager", "001", "Branch Manager", "EMP-1001", 0, 0),
        new("supervisor@corebank.local", "Nazmul Haque", "Supervisor", "002", "Operations Supervisor", "EMP-1002", 0, 0),
        new("officer@corebank.local", "Shamima Akter", "Officer", "002", "Senior Officer", "EMP-1003", 2000000, 2000000),
        new("teller@corebank.local", "Rashed Khan", "Teller", "002", "Teller", "EMP-1004", 500000, 200000),
        new("officer.gulshan@corebank.local", "Imran Hossain", "Officer", "003", "Officer", "EMP-1005", 2000000, 2000000),
        new("teller.ctg@corebank.local", "Mitu Das", "Teller", "004", "Teller", "EMP-1006", 500000, 200000),
        new("supervisor.sylhet@corebank.local", "Farzana Islam", "Supervisor", "005", "Operations Supervisor", "EMP-1007", 0, 0),
    ];

    public static readonly (string Code, string Name, string Layer, string Parent, string Address, string Phone, string Routing, bool Active)[] Branches =
    [
        ("001", "Head Office", "HO", "", "Motijheel C/A, Dhaka-1000", "+880 2-9551100", "200270001", true),
        ("002", "Motijheel Branch", "Branch", "001", "Dilkusha C/A, Motijheel, Dhaka-1000", "+880 2-9551200", "200270002", true),
        ("003", "Gulshan Branch", "Branch", "001", "Gulshan Avenue, Gulshan-1, Dhaka-1212", "+880 2-9881300", "200270003", true),
        ("004", "Agrabad Branch", "Branch", "001", "Sheikh Mujib Road, Agrabad, Chattogram-4100", "+880 31-2510400", "200270004", true),
        ("005", "Zindabazar Branch", "Branch", "001", "Zindabazar, Sylhet-3100", "+880 821-720500", "200270005", true),
        ("006", "Mirpur Sub-Branch", "SubBranch", "002", "Section-10, Mirpur, Dhaka-1216", "+880 2-9001600", "200270006", false),
    ];

    public static readonly string[][] Areas =
    [
        ["Dhanmondi, Dhaka", "Uttara, Dhaka", "Mirpur, Dhaka"],
        ["Motijheel, Dhaka", "Malibagh, Dhaka", "Khilgaon, Dhaka"],
        ["Gulshan, Dhaka", "Banani, Dhaka", "Baridhara, Dhaka"],
        ["Agrabad, Chattogram", "Nasirabad, Chattogram", "Khulshi, Chattogram"],
        ["Zindabazar, Sylhet", "Amberkhana, Sylhet", "Subid Bazar, Sylhet"],
    ];

    public static readonly string[] Banks = ["Sonali Bank PLC", "Janata Bank PLC", "BRAC Bank PLC", "Dutch-Bangla Bank PLC", "City Bank PLC", "Islami Bank Bangladesh PLC"];

    public static readonly string[] Beneficiaries =
    [
        "Md. Alamgir Hossain", "Nasreen Sultana", "Rahman Enterprise", "Delwar Hossain & Sons", "Sumon Trading Corporation",
        "Mst. Rabeya Begum", "Greenland Suppliers", "Habib Ullah Khan", "Tasnim Fabrics", "Kazi Mohammad Shahin"
    ];

    public static readonly (string Name, string Aliases, string Id, string Notes, Models.Entities.SanctionSource Source)[] Sanctions =
    [
        ("Zulqarnain Abdul Qadir Siddiqui", "Z. A. Siddiqui; Abu Zulqarnain", "UN-QDi.901", "UNSCR 1267 - fictitious demo entry", Models.Entities.SanctionSource.UNSCR),
        ("Hamdan Yusuf Al-Mansoori", "Hamdan Al Mansouri", "UN-QDi.902", "UNSCR 1267 - fictitious demo entry", Models.Entities.SanctionSource.UNSCR),
        ("Tariq Mehboob Qureshi", "Tariq M. Qureshi", "UN-TAi.303", "UNSCR 1988 - fictitious demo entry", Models.Entities.SanctionSource.UNSCR),
        ("Vladlen Orsky Petrovich", "", "UN-RUi.404", "UNSCR 1718 - fictitious demo entry", Models.Entities.SanctionSource.UNSCR),
        ("Nurul Amin Shikder Mintu", "Mintu Shikder", "BD-LOC-0011", "Local list - fictitious demo entry", Models.Entities.SanctionSource.Local),
        ("Salauddin Kabir Nayan", "", "BD-LOC-0012", "Local list - fictitious demo entry", Models.Entities.SanctionSource.Local),
        ("Abu Hanif Jabbar Mollah", "Hanif Mollah", "BD-LOC-0013", "Local list - fictitious demo entry", Models.Entities.SanctionSource.Local),
    ];
}
