using System;
using System.Collections.Generic;
using System.Linq;

namespace Mazandasht.Desktop.Import
{
    /// <summary>
    /// What a committed import row becomes.
    ///  - CatalogParty / CatalogProduct: a base-data record (no document).
    ///  - Document: a DRAFT document with lines. It carries no financial or
    ///    inventory effect until a module engine posts it after manager approval.
    /// </summary>
    public enum ImportTargetKind
    {
        CatalogParty = 0,
        CatalogProduct = 1,
        Document = 2
    }

    public sealed class ImportTarget
    {
        public string ProfileCode { get; set; }
        public string DocumentType { get; set; }
        public ImportTargetKind Kind { get; set; }
        public string KeyField { get; set; }
        public string DateField { get; set; }
        public string PartyField { get; set; }
        public string ProductField { get; set; }
        public string WarehouseField { get; set; }
        public string SourceDocumentField { get; set; }
        public string RequiredField1 { get; set; }
        public string RequiredField2 { get; set; }
        public string RequiredField3 { get; set; }
        public string RequiredField4 { get; set; }
        public string RequiredField5 { get; set; }
        public string RequiredField6 { get; set; }
        public string RequiredField7 { get; set; }
        public string RequiredField8 { get; set; }
    }

    /// <summary>
    /// The commit-side contract for the eleven import profiles. The required
    /// fields mirror docs/11_Import_Profiles_Canonical.json; the G6 tests assert
    /// that this table and the canonical contract agree.
    /// </summary>
    public static class ImportTargetCatalog
    {
        private static readonly IList<ImportTarget> Targets = new List<ImportTarget>
        {
            new ImportTarget {
                ProfileCode = "MASTER_PARTIES", Kind = ImportTargetKind.CatalogParty,
                KeyField = "کد شخص", RequiredField1 = "نام نمایشی", RequiredField2 = "نوع شخص" },
            new ImportTarget {
                ProfileCode = "MASTER_PRODUCTS", Kind = ImportTargetKind.CatalogProduct,
                KeyField = "کد محصول", RequiredField1 = "نام محصول",
                RequiredField2 = "کد گروه محصول", RequiredField3 = "کد واحد" },
            new ImportTarget {
                ProfileCode = "WEIGHING_IN", Kind = ImportTargetKind.Document,
                DocumentType = "WEIGHING", KeyField = "کد سند منبع", DateField = "تاریخ شمسی",
                PartyField = "کد طرف حساب", ProductField = "کد محصول", WarehouseField = "کد انبار",
                RequiredField1 = "جهت", RequiredField2 = "وزن ناخالص (kg)", RequiredField3 = "وزن خالص (kg)" },
            new ImportTarget {
                ProfileCode = "PURCHASE_INVOICES", Kind = ImportTargetKind.Document,
                DocumentType = "PURCHASE", KeyField = "کد فاکتور منبع", DateField = "تاریخ شمسی",
                PartyField = "کد فروشنده", ProductField = "کد محصول",
                SourceDocumentField = "کد باسکول ورودی",
                RequiredField1 = "وزن خرید (kg)", RequiredField2 = "فی خرید (ریال/kg)" },
            new ImportTarget {
                ProfileCode = "SALES_INVOICES", Kind = ImportTargetKind.Document,
                DocumentType = "SALE", KeyField = "کد فاکتور منبع", DateField = "تاریخ شمسی",
                PartyField = "کد خریدار", ProductField = "کد محصول",
                SourceDocumentField = "کد محموله",
                RequiredField1 = "وزن فروش (kg)", RequiredField2 = "فی فروش (ریال/kg)" },
            new ImportTarget {
                ProfileCode = "SORTING", Kind = ImportTargetKind.Document,
                DocumentType = "SORTING", KeyField = "کد سند سورت", DateField = "تاریخ شمسی",
                ProductField = "کد محصول خروجی", WarehouseField = "کد انبار خروجی",
                SourceDocumentField = "کد محموله ورودی",
                RequiredField1 = "کد انبار ورودی", RequiredField2 = "وزن ورودی (kg)",
                RequiredField3 = "وزن خروجی (kg)" },
            new ImportTarget {
                ProfileCode = "BASKET_MOVEMENTS", Kind = ImportTargetKind.Document,
                DocumentType = "BASKET", KeyField = "کد سند منبع", DateField = "تاریخ شمسی",
                RequiredField1 = "نوع عملیات", RequiredField2 = "کد نوع سبد", RequiredField3 = "تعداد" },
            new ImportTarget {
                ProfileCode = "BANK_STATEMENT", Kind = ImportTargetKind.Document,
                DocumentType = "BANK_TXN", KeyField = "کد تراکنش منبع", DateField = "تاریخ شمسی",
                RequiredField1 = "کد حساب بانکی", RequiredField2 = "جهت", RequiredField3 = "مبلغ اصلی (ریال)" },
            new ImportTarget {
                ProfileCode = "CHEQUES", Kind = ImportTargetKind.Document,
                DocumentType = "CHEQUE", KeyField = "کد سند منبع",
                PartyField = "کد طرف حساب",
                RequiredField1 = "جهت چک", RequiredField2 = "شماره چک", RequiredField3 = "تاریخ صدور",
                RequiredField4 = "تاریخ سررسید", RequiredField5 = "مبلغ (ریال)" },
            new ImportTarget {
                ProfileCode = "SETTLEMENTS", Kind = ImportTargetKind.Document,
                DocumentType = "OTHER", KeyField = "کد سند تخصیص منبع",
                SourceDocumentField = "شماره سند پرداخت",
                RequiredField1 = "شماره فاکتور", RequiredField2 = "مبلغ تخصیص (ریال)",
                RequiredField3 = "نوع ابزار" },
            new ImportTarget {
                ProfileCode = "PARTNER_OPERATIONS", Kind = ImportTargetKind.Document,
                DocumentType = "WEIGHING", KeyField = "کد ردیف منبع", DateField = "تاریخ شمسی",
                PartyField = "کد شریک", RequiredField1 = "نوع عملیات" }
        };

        public static IList<ImportTarget> All
        {
            get { return Targets.ToList(); }
        }

        public static ImportTarget Find(string profileCode)
        {
            return Targets.FirstOrDefault(x => String.Equals(
                x.ProfileCode, profileCode, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Required fields for a profile, in contract order, never null.
        /// </summary>
        public static IList<string> RequiredFields(ImportTarget target)
        {
            var fields = new List<string> { target.KeyField };
            foreach (var name in new[] {
                target.DateField, target.PartyField, target.ProductField, target.WarehouseField,
                target.SourceDocumentField,
                target.RequiredField1, target.RequiredField2, target.RequiredField3,
                target.RequiredField4, target.RequiredField5, target.RequiredField6,
                target.RequiredField7, target.RequiredField8 })
                if (!String.IsNullOrWhiteSpace(name)) fields.Add(name);
            return fields;
        }

        public static string DocumentTypeFor(string profileCode)
        {
            var target = Find(profileCode);
            return target == null ? null : target.DocumentType;
        }
    }
}
