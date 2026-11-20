using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Mazandasht.Desktop.Import
{
    public sealed class ImportFieldDefinition
    {
        public string TargetField { get; set; }
        public bool Required { get; set; }
        public string Format { get; set; }
        public string Description { get; set; }
    }

    public sealed class ImportSourceColumn
    {
        public int Index { get; set; }
        public string Header { get; set; }
        public string DisplayName { get; set; }
    }

    public sealed class ImportColumnMapping
    {
        public string TargetField { get; set; }
        public bool Required { get; set; }
        public int? SourceColumnIndex { get; set; }
        public string SourceHeader { get; set; }
    }

    public sealed class ImportProfile
    {
        public string Code { get; set; }
        public string DisplayName { get; set; }
        public string TemplateFile { get; set; }
        public string Description { get; set; }
        public override string ToString() { return DisplayName; }
    }

    public static class ImportProfileCatalog
    {
        private static readonly IList<ImportProfile> Profiles = new List<ImportProfile>
        {
            new ImportProfile { Code="MASTER_PARTIES", DisplayName="اطلاعات پایه | اشخاص", TemplateFile="01_Parties.xlsx", Description="اشخاص حقیقی و حقوقی؛ بدون ایجاد مانده افتتاحیه خودکار" },
            new ImportProfile { Code="MASTER_PRODUCTS", DisplayName="اطلاعات پایه | محصولات", TemplateFile="02_Products.xlsx", Description="محصولات، گروه، واحد و کد مرجع" },
            new ImportProfile { Code="WEIGHING_IN", DisplayName="عملیات | باسکول ورودی", TemplateFile="03_Weighing_In.xlsx", Description="ورود بار و اتصال به طرف حساب، محصول و انبار" },
            new ImportProfile { Code="PURCHASE_INVOICES", DisplayName="بازرگانی | فاکتور خرید", TemplateFile="04_Purchase_Invoices.xlsx", Description="فاکتور خرید با مرجع باسکول یا محموله؛ بدون ثبت خودکار پرداخت" },
            new ImportProfile { Code="SALES_INVOICES", DisplayName="بازرگانی | فاکتور فروش", TemplateFile="05_Sales_Invoices.xlsx", Description="فاکتور فروش با مرجع محموله و اطلاعات مالی" },
            new ImportProfile { Code="SORTING", DisplayName="عملیات | سورتینگ", TemplateFile="06_Sorting.xlsx", Description="ورودی، خروجی‌های درجه‌بندی، ضایعات و افت" },
            new ImportProfile { Code="BASKET_MOVEMENTS", DisplayName="عملیات | گردش سبد", TemplateFile="07_Basket_Movements.xlsx", Description="خرید، تحویل امانی، برگشت و انتقال سبد" },
            new ImportProfile { Code="BANK_STATEMENT", DisplayName="مالی | گردش بانک", TemplateFile="08_Bank_Transactions.xlsx", Description="واریز، برداشت، کارمزد و شماره پیگیری" },
            new ImportProfile { Code="CHEQUES", DisplayName="مالی | چک", TemplateFile="09_Cheques.xlsx", Description="چک دریافتی/پرداختی و سررسید" },
            new ImportProfile { Code="SETTLEMENTS", DisplayName="مالی | تخصیص تسویه", TemplateFile="10_Settlements.xlsx", Description="اتصال پرداخت‌های ثبت‌شده به فاکتورها" },
            new ImportProfile { Code="PARTNER_OPERATIONS", DisplayName="شراکت | ورود بار و سبد", TemplateFile="11_Partnership.xlsx", Description="ورود بار شراکتی، سهم‌ها و گردش سبد امانی" }
        };

        public static IList<ImportProfile> All { get { return Profiles.ToList(); } }

        public static ImportProfile Find(string code)
        {
            return Profiles.FirstOrDefault(x => String.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
        }

        public static IList<ImportFieldDefinition> GetFieldDefinitions(string code)
        {
            var preview = ImportWorkbookReader.ReadPreview(TemplatePath(code), "فرهنگ داده");
            var fields = new List<ImportFieldDefinition>();
            foreach (var row in preview.Rows)
            {
                if (row.RowNumber == 1 || row.Cells.Length < 4 || String.IsNullOrWhiteSpace(row.Cells[0])) continue;
                fields.Add(new ImportFieldDefinition
                {
                    TargetField = row.Cells[0].Trim(),
                    Required = String.Equals((row.Cells[1] ?? String.Empty).Trim(), "بله", StringComparison.Ordinal),
                    Format = row.Cells[2],
                    Description = row.Cells[3]
                });
            }
            if (fields.Count == 0) throw new InvalidOperationException("فرهنگ داده قالب Excel خالی یا نامعتبر است.");
            return fields;
        }

        public static string TemplatePath(string code)
        {
            var profile = Find(code);
            if (profile == null) throw new ArgumentException("نوع ورود اطلاعات معتبر نیست.");
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Templates", profile.TemplateFile);
            if (!File.Exists(path)) throw new FileNotFoundException("قالب Excel این ماژول در کنار نرم‌افزار پیدا نشد.", path);
            return path;
        }
    }
}
