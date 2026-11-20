using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Mazandasht.Desktop.Services;

namespace Mazandasht.Desktop.Views
{
    /// <summary>
    /// Finance modules completed in G8: purchase invoices, sales invoices, bank
    /// transactions, cheques and settlement allocation. Every write creates a
    /// REVIEW document; no journal line is produced in G8 (double-entry posting is
    /// G9) and no stock movement is posted.
    /// </summary>
    public sealed class FinanceModulesWindow : Window
    {
        private readonly string _db;
        private readonly string _userId;

        // Purchase
        private ComboBox _purchaseParty, _purchaseWeigh, _purchaseProduct;
        private TextBox _purchaseQty, _purchasePrice, _purchaseDiscount, _purchaseFreight,
            _purchaseCommission, _purchaseOther, _purchaseDate, _purchaseReference;
        private DataGrid _purchaseLines, _purchaseList;

        // Sales
        private ComboBox _salesParty, _salesProduct, _salesGrade, _salesPackaging, _salesWeigh;
        private TextBox _salesQty, _salesPrice, _salesDiscount, _salesFreight, _salesCommission,
            _salesOther, _salesDate, _salesReference;
        private DataGrid _salesLines, _salesList, _salesAvailable;

        // Bank
        private ComboBox _bankAccount, _bankDirection, _bankParty, _bankType;
        private TextBox _bankAmount, _bankFee, _bankStatement, _bankTracking, _bankDate;
        private DataGrid _bankList, _bankAccounts;

        // Cheque
        private ComboBox _chequeDirection, _chequeBank, _chequeBeneficiary;
        private TextBox _chequeNumber, _chequeIssue, _chequeDue, _chequeAmount;
        private DataGrid _chequeList;

        // Settlement
        private ComboBox _settlePayment, _settleInvoice;
        private TextBox _settleAmount;
        private TextBlock _settleRemaining;
        private DataGrid _settleList;

        public FinanceModulesWindow(string databasePath, string userId)
        {
            _db = databasePath;
            _userId = userId;
            Title = "خرید، فروش، بانک، چک و تسویه | مازندشت";
            Width = 1240; Height = 820;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FlowDirection = FlowDirection.RightToLeft;
            Background = ThemeService.Brush("WindowBackgroundBrush");
            Foreground = ThemeService.Brush("TextBrush");

            var root = new DockPanel { Margin = new Thickness(22) };
            var header = new TextBlock {
                Text = "عملیات مالی: خرید، فروش، بانک، چک و تسویه",
                FontSize = 22, FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 16),
                Foreground = ThemeService.Brush("TextBrush")
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var tabs = new TabControl();
            tabs.Items.Add(BuildPurchaseTab());
            tabs.Items.Add(BuildSalesTab());
            tabs.Items.Add(BuildBankTab());
            tabs.Items.Add(BuildChequeTab());
            tabs.Items.Add(BuildSettlementTab());
            root.Children.Add(tabs);

            Content = root;
            RefreshAll();
        }

        // ------------------------------------------------------------------
        private TabItem BuildPurchaseTab()
        {
            var panel = NewPanel();
            panel.Children.Add(Note("فاکتور خرید صافی هر خط را با فرمول «وزن × فی ÷ ۱۰۰۰ − تخفیف + کرایه + کمیسیون + سایر تعدیلات» می‌سازد و خط را به باسکول ورودی مرتبط می‌کند."));
            var form = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _purchaseParty = Combo(form, "فروشنده", MasterDataService.Parties(_db), false);
            _purchaseWeigh = Combo(form, "باسکول ورودی", Documents(_db, "WEIGHING"), true);
            _purchaseProduct = Combo(form, "محصول", MasterDataService.Products(_db), false);
            _purchaseQty = Field(form, "وزن (گرم)", 110);
            _purchasePrice = Field(form, "فی (ریال/kg)", 130);
            _purchaseDiscount = Field(form, "تخفیف (ریال)", 120);
            _purchaseFreight = Field(form, "کرایه (ریال)", 110);
            _purchaseCommission = Field(form, "کمیسیون (ریال)", 120);
            _purchaseOther = Field(form, "سایر تعدیلات", 120);
            _purchaseDate = Field(form, "تاریخ", 110);
            _purchaseReference = Field(form, "شماره فاکتور منبع", 150);
            _purchaseDate.Text = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            form.Children.Add(ActionButton("＋ افزودن خط", AddPurchaseLine));
            form.Children.Add(ActionButton("＋ ثبت فاکتور خرید", CreatePurchase));
            panel.Children.Add(form);

            panel.Children.Add(new TextBlock { Text = "خطوط فاکتور", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 6, 0, 4) });
            _purchaseLines = Grid();
            panel.Children.Add(_purchaseLines);
            panel.Children.Add(new TextBlock { Text = "فاکتورهای خرید ثبت‌شده", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 10, 0, 4) });
            _purchaseList = Grid();
            panel.Children.Add(_purchaseList);
            return new TabItem { Header = "خرید", Content = panel };
        }

        private TabItem BuildSalesTab()
        {
            var panel = NewPanel();
            panel.Children.Add(Note("فروش فقط از موجودی تأییدشده و به‌صورت FIFO تخصیص می‌یابد؛ فروش بیش از موجودی مجاز نیست. تخصیص، رزرو است و تا تأیید سند موجودی را تغییر نمی‌دهد."));
            var form = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _salesParty = Combo(form, "خریدار", MasterDataService.Parties(_db), false);
            _salesProduct = Combo(form, "محصول", MasterDataService.Products(_db), false);
            _salesGrade = Combo(form, "درجه", new List<CatalogRow>(), true, "DisplayName");
            _salesPackaging = Combo(form, "بسته‌بندی", MasterDataService.Packaging(_db), true);
            _salesWeigh = Combo(form, "باسکول منبع", Documents(_db, "WEIGHING"), true);
            _salesQty = Field(form, "وزن (گرم)", 110);
            _salesPrice = Field(form, "فی (ریال/kg)", 130);
            _salesDiscount = Field(form, "تخفیف (ریال)", 120);
            _salesFreight = Field(form, "کرایه (ریال)", 110);
            _salesCommission = Field(form, "کمیسیون (ریال)", 120);
            _salesOther = Field(form, "سایر هزینه‌ها", 120);
            _salesDate = Field(form, "تاریخ", 110);
            _salesReference = Field(form, "شماره فاکتور منبع", 150);
            _salesDate.Text = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            form.Children.Add(ActionButton("＋ افزودن خط", AddSalesLine));
            form.Children.Add(ActionButton("＋ ثبت فاکتور فروش", CreateSales));
            panel.Children.Add(form);

            panel.Children.Add(new TextBlock { Text = "موجودی قابل فروش (FIFO)", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 6, 0, 4) });
            _salesAvailable = Grid();
            panel.Children.Add(_salesAvailable);
            panel.Children.Add(new TextBlock { Text = "خطوط فاکتور", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 6, 0, 4) });
            _salesLines = Grid();
            panel.Children.Add(_salesLines);
            panel.Children.Add(new TextBlock { Text = "فاکتورهای فروش ثبت‌شده", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 10, 0, 4) });
            _salesList = Grid();
            panel.Children.Add(_salesList);
            _salesProduct.SelectionChanged += (s, e) => RefreshSalesGrades();
            return new TabItem { Header = "فروش", Content = panel };
        }

        private TabItem BuildBankTab()
        {
            var panel = NewPanel();
            panel.Children.Add(Note("هر تراکنش بانکی یک سند BANK_TXN و یک ردیف payment_transactions می‌سازد. حساب بانکی را می‌توان با کد حساب، کد دفتر حساب یا عنوان حساب وارد کرد."));
            var form = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _bankAccount = Combo(form, "حساب بانکی", BankAccounts(), false);
            _bankDirection = Combo(form, "جهت", Directions(), false);
            _bankParty = Combo(form, "طرف حساب", MasterDataService.Parties(_db), true);
            _bankType = Combo(form, "نوع تراکنش", TransactionTypes(), true);
            _bankAmount = Field(form, "مبلغ (ریال)", 130);
            _bankFee = Field(form, "کارمزد (ریال)", 120);
            _bankStatement = Field(form, "مانده پس از تراکنش", 140);
            _bankTracking = Field(form, "شماره پیگیری", 130);
            _bankDate = Field(form, "تاریخ", 110);
            _bankDate.Text = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            form.Children.Add(ActionButton("＋ ثبت تراکنش بانکی", CreateBankTransaction));
            panel.Children.Add(form);

            panel.Children.Add(new TextBlock { Text = "حساب‌های بانکی و مانده", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 8, 0, 4) });
            _bankAccounts = Grid();
            panel.Children.Add(_bankAccounts);
            panel.Children.Add(new TextBlock { Text = "تراکنش‌های ثبت‌شده", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 10, 0, 4) });
            _bankList = Grid();
            panel.Children.Add(_bankList);
            return new TabItem { Header = "بانک", Content = panel };
        }

        private TabItem BuildChequeTab()
        {
            var panel = NewPanel();
            panel.Children.Add(Note("وصول چک یک سند BANK_TXN و یک تراکنش بانکی می‌سازد تا مانده بانک دقیقاً یک بار جابه‌جا شود. تغییر وضعیت چک فقط رویداد ثبت می‌کند."));
            var form = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _chequeDirection = Combo(form, "جهت چک", Directions(), false);
            _chequeBank = Combo(form, "حساب بانکی", BankAccounts(), true);
            _chequeBeneficiary = Combo(form, "طرف حساب", MasterDataService.Parties(_db), true);
            _chequeNumber = Field(form, "شماره چک", 130);
            _chequeIssue = Field(form, "تاریخ صدور", 110);
            _chequeDue = Field(form, "تاریخ سررسید", 110);
            _chequeAmount = Field(form, "مبلغ (ریال)", 130);
            _chequeIssue.Text = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            _chequeDue.Text = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            form.Children.Add(ActionButton("＋ ثبت چک", CreateCheque));
            panel.Children.Add(form);

            panel.Children.Add(new TextBlock { Text = "چک‌های ثبت‌شده", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 8, 0, 4) });
            _chequeList = Grid();
            panel.Children.Add(_chequeList);
            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            actions.Children.Add(ActionButton("وصول چک انتخاب‌شده", ClearCheque));
            actions.Children.Add(ActionButton("برگشت چک", MarkReturned));
            actions.Children.Add(ActionButton("باطل‌شدن چک", MarkCancelled));
            panel.Children.Add(actions);
            return new TabItem { Header = "چک", Content = panel };
        }

        private TabItem BuildSettlementTab()
        {
            var panel = NewPanel();
            panel.Children.Add(Note("تخصیص پرداخت به فاکتور بدون ساختن تراکنش بانکی جدید انجام می‌شود و مجموع تخصیص‌ها نمی‌تواند از مبلغ پرداخت بیشتر شود."));
            var form = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _settlePayment = Combo(form, "سند پرداخت", Documents(_db, "BANK_TXN"), false);
            _settleInvoice = Combo(form, "فاکتور", InvoiceDocuments(), false);
            _settleAmount = Field(form, "مبلغ تخصیص (ریال)", 150);
            form.Children.Add(ActionButton("＋ ثبت تخصیص", AllocateSettlement));
            panel.Children.Add(form);
            _settleRemaining = new TextBlock { Margin = new Thickness(5),
                Foreground = ThemeService.Brush("MutedTextBrush") };
            panel.Children.Add(_settleRemaining);

            panel.Children.Add(new TextBlock { Text = "تخصیص‌های ثبت‌شده", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 8, 0, 4) });
            _settleList = Grid();
            panel.Children.Add(_settleList);
            _settlePayment.SelectionChanged += (s, e) => RefreshRemaining();
            return new TabItem { Header = "تسویه", Content = panel };
        }

        // ------------------------------------------------------------------
        private StackPanel NewPanel() { return new StackPanel { Margin = new Thickness(14) }; }

        private TextBlock Note(string text)
        {
            return new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeService.Brush("MutedTextBrush"), Margin = new Thickness(4, 0, 4, 10) };
        }

        private TextBox Field(Panel parent, string label, double width)
        {
            var box = new TextBox { Width = width, Height = 34, Margin = new Thickness(5),
                VerticalContentAlignment = VerticalAlignment.Center, ToolTip = label,
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                BorderBrush = ThemeService.Brush("GlassBorderBrush") };
            parent.Children.Add(box);
            return box;
        }

        private ComboBox Combo(Panel parent, string label, IEnumerable<object> items, bool optional,
            string displayMember = "Display")
        {
            var list = new List<object>(items);
            if (optional) list.Insert(0, new LookupItem { Id = "", Name = "—" });
            var combo = new ComboBox { Width = 210, Height = 34, Margin = new Thickness(5),
                DisplayMemberPath = displayMember, SelectedValuePath = "Id", ItemsSource = list,
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"), ToolTip = label };
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
            parent.Children.Add(combo);
            return combo;
        }

        private Button ActionButton(string text, RoutedEventHandler click)
        {
            var button = new Button { Content = text, Height = 36, Margin = new Thickness(5),
                Padding = new Thickness(14, 5, 14, 5),
                Background = ThemeService.Brush("AccentBrush"),
                Foreground = Brushes.White, BorderThickness = new Thickness(0) };
            button.Click += click;
            return button;
        }

        private DataGrid Grid()
        {
            return new DataGrid { AutoGenerateColumns = true, IsReadOnly = true,
                CanUserAddRows = false, Margin = new Thickness(0, 8, 0, 0),
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
        }

        // ------------------------------------------------------------------
        private static List<LookupItem> Directions()
        {
            return new List<LookupItem> {
                new LookupItem { Id = "IN", Name = "ورود" },
                new LookupItem { Id = "OUT", Name = "خروج" }
            };
        }

        private static List<LookupItem> Documents(string databasePath, string documentType)
        {
            var rows = new List<LookupItem>();
            using (var connection = new SQLiteConnection(
                "Data Source=" + databasePath + ";Version=3;Pooling=True;"))
            {
                connection.Open();
                using (var command = new SQLiteCommand(
                    "SELECT id,document_number,date_iso FROM documents WHERE document_type=@type "
                    + "AND status IN ('DRAFT','REVIEW') ORDER BY created_at_utc DESC LIMIT 300;", connection))
                {
                    command.Parameters.AddWithValue("@type", documentType);
                    using (var reader = command.ExecuteReader())
                        while (reader.Read())
                            rows.Add(new LookupItem { Id = reader.GetString(0), Code = reader.GetString(1),
                                Name = reader.GetString(1) + " | " + reader.GetString(2) });
                }
            }
            return rows;
        }

        private List<LookupItem> InvoiceDocuments()
        {
            var rows = new List<LookupItem>();
            using (var connection = new SQLiteConnection(
                "Data Source=" + _db + ";Version=3;Pooling=True;"))
            {
                connection.Open();
                using (var command = new SQLiteCommand(
                    "SELECT id,document_number FROM documents WHERE document_type IN ('PURCHASE','SALE') "
                    + "ORDER BY created_at_utc DESC LIMIT 300;", connection))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        rows.Add(new LookupItem { Id = reader.GetString(0), Code = reader.GetString(1),
                            Name = reader.GetString(1) });
            }
            return rows;
        }

        private List<LookupItem> TransactionTypes()
        {
            var rows = new List<LookupItem>();
            using (var connection = new SQLiteConnection(
                "Data Source=" + _db + ";Version=3;Pooling=True;"))
            {
                connection.Open();
                using (var command = new SQLiteCommand(
                    "SELECT id,COALESCE(type_code,'—'),display_name FROM transaction_types "
                    + "WHERE is_active=1 ORDER BY display_name;", connection))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        rows.Add(new LookupItem { Id = reader.GetString(0), Code = reader.GetString(1),
                            Name = reader.GetString(2) });
            }
            return rows;
        }

        private List<LookupItem> BankAccounts()
        {
            return BankService.Accounts(_db).Select(x => new LookupItem {
                Id = x.Id, Code = x.BankName, Name = x.Title
            }).ToList();
        }

        // ------------------------------------------------------------------
        private void RefreshAll()
        {
            _purchaseList.ItemsSource = PurchaseService.List(_db);
            _salesList.ItemsSource = SalesService.List(_db);
            _bankList.ItemsSource = BankService.List(_db);
            _bankAccounts.ItemsSource = BankService.Accounts(_db);
            _chequeList.ItemsSource = ChequeService.List(_db);
            _settleList.ItemsSource = SettlementService.List(_db);
            RefreshSalesGrades();
            RefreshRemaining();
        }

        private void RefreshSalesGrades()
        {
            var productId = _salesProduct.SelectedValue as string;
            _salesGrade.ItemsSource = String.IsNullOrEmpty(productId)
                ? new List<CatalogRow>() : BaseDataService.ProductGrades(_db, productId);
            if (_salesGrade.Items.Count > 0) _salesGrade.SelectedIndex = 0;
            _salesAvailable.ItemsSource = String.IsNullOrEmpty(productId)
                ? null : SalesService.AvailableLots(_db, productId);
        }

        private void RefreshRemaining()
        {
            var paymentId = _settlePayment.SelectedValue as string;
            if (String.IsNullOrEmpty(paymentId))
            {
                _settleRemaining.Text = "—";
                return;
            }
            try { _settleRemaining.Text = "باقی‌مانده: " + SettlementService.Remaining(_db, paymentId).ToString("N0") + " ریال"; }
            catch (Exception) { _settleRemaining.Text = "—"; }
        }

        // ------------------------------------------------------------------
        private static string Optional(string value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static long Grams(string value, string label)
        {
            var text = NormalizeDigits(value);
            long grams;
            if (!Int64.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out grams) || grams < 0)
                throw new ArgumentException(label + " باید عدد صحیح نامنفی باشد.");
            return grams;
        }

        private static long Rials(string value, string label)
        {
            return Grams(value, label);
        }

        private static string NormalizeDigits(string value)
        {
            if (String.IsNullOrEmpty(value)) return value;
            var chars = value.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (chars[i] >= '۰' && chars[i] <= '۹') chars[i] = (char)('0' + (chars[i] - '۰'));
                else if (chars[i] >= '٠' && chars[i] <= '٩') chars[i] = (char)('0' + (chars[i] - '٠'));
                else if (chars[i] == '٫') chars[i] = '.';
                else if (chars[i] == '٬') chars[i] = ',';
            }
            return new string(chars);
        }

        private void ShowError(Exception exception, string title)
        {
            MessageBox.Show(exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Purchase -----------------------------------------------------------------
        private sealed class PurchaseLineRow
        {
            public string ProductId { get; set; }
            public string SourceWeighDocumentId { get; set; }
            public long QuantityGrams { get; set; }
            public long UnitPriceIrrPerKg { get; set; }
            public long DiscountIrr { get; set; }
            public long FreightIrr { get; set; }
            public long CommissionIrr { get; set; }
            public long OtherAdjustmentsIrr { get; set; }
        }

        private readonly List<PurchaseLineRow> _purchaseRows = new List<PurchaseLineRow>();

        private void AddPurchaseLine(object sender, RoutedEventArgs e)
        {
            _purchaseRows.Add(new PurchaseLineRow {
                ProductId = _purchaseProduct.SelectedValue as string,
                SourceWeighDocumentId = Optional(_purchaseWeigh.SelectedValue as string),
                QuantityGrams = Grams(_purchaseQty.Text, "وزن"),
                UnitPriceIrrPerKg = Rials(_purchasePrice.Text, "فی"),
                DiscountIrr = Rials(_purchaseDiscount.Text, "تخفیف"),
                FreightIrr = Rials(_purchaseFreight.Text, "کرایه"),
                CommissionIrr = Rials(_purchaseCommission.Text, "کمیسیون"),
                OtherAdjustmentsIrr = Rials(_purchaseOther.Text, "سایر تعدیلات")
            });
            _purchaseLines.ItemsSource = null;
            _purchaseLines.ItemsSource = _purchaseRows;
        }

        private void CreatePurchase(object sender, RoutedEventArgs e)
        {
            try
            {
                var draft = new PurchaseInvoiceDraft {
                    PartyAccountId = _purchaseParty.SelectedValue as string,
                    DateIso = _purchaseDate.Text,
                    SourceWeighDocumentId = Optional(_purchaseWeigh.SelectedValue as string),
                    ReferenceNumber = Optional(_purchaseReference.Text),
                    Lines = _purchaseRows.Select(x => new PurchaseInvoiceLine {
                        ProductId = x.ProductId, SourceWeighDocumentId = x.SourceWeighDocumentId,
                        QuantityGrams = x.QuantityGrams, UnitPriceIrrPerKg = x.UnitPriceIrrPerKg,
                        DiscountIrr = x.DiscountIrr, FreightIrr = x.FreightIrr,
                        CommissionIrr = x.CommissionIrr, OtherAdjustmentsIrr = x.OtherAdjustmentsIrr
                    }).ToList()
                };
                var id = PurchaseService.SaveDraft(_db, draft, _userId);
                MessageBox.Show("فاکتور خرید با شناسه داخلی " + id + " در وضعیت بررسی ثبت شد.", "مازندشت",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshAll();
            }
            catch (Exception ex) { ShowError(ex, "ثبت فاکتور خرید"); }
        }

        // Sales -----------------------------------------------------------------
        private sealed class SalesLineRow
        {
            public string ProductId { get; set; }
            public string GradeId { get; set; }
            public string PackagingTypeId { get; set; }
            public string SourceWeighDocumentId { get; set; }
            public long QuantityGrams { get; set; }
            public long UnitPriceIrrPerKg { get; set; }
            public long DiscountIrr { get; set; }
            public long FreightIrr { get; set; }
            public long CommissionIrr { get; set; }
            public long OtherCostsIrr { get; set; }
        }

        private readonly List<SalesLineRow> _salesRows = new List<SalesLineRow>();

        private void AddSalesLine(object sender, RoutedEventArgs e)
        {
            _salesRows.Add(new SalesLineRow {
                ProductId = _salesProduct.SelectedValue as string,
                GradeId = Optional(_salesGrade.SelectedValue as string),
                PackagingTypeId = Optional(_salesPackaging.SelectedValue as string),
                SourceWeighDocumentId = Optional(_salesWeigh.SelectedValue as string),
                QuantityGrams = Grams(_salesQty.Text, "وزن"),
                UnitPriceIrrPerKg = Rials(_salesPrice.Text, "فی"),
                DiscountIrr = Rials(_salesDiscount.Text, "تخفیف"),
                FreightIrr = Rials(_salesFreight.Text, "کرایه"),
                CommissionIrr = Rials(_salesCommission.Text, "کمیسیون"),
                OtherCostsIrr = Rials(_salesOther.Text, "سایر هزینه‌ها")
            });
            _salesLines.ItemsSource = null;
            _salesLines.ItemsSource = _salesRows;
        }

        private void CreateSales(object sender, RoutedEventArgs e)
        {
            try
            {
                var draft = new SalesInvoiceDraft {
                    PartyAccountId = _salesParty.SelectedValue as string,
                    DateIso = _salesDate.Text,
                    ReferenceNumber = Optional(_salesReference.Text),
                    Lines = _salesRows.Select(x => new SalesInvoiceLine {
                        ProductId = x.ProductId, GradeId = x.GradeId, PackagingTypeId = x.PackagingTypeId,
                        SourceWeighDocumentId = x.SourceWeighDocumentId,
                        QuantityGrams = x.QuantityGrams, UnitPriceIrrPerKg = x.UnitPriceIrrPerKg,
                        DiscountIrr = x.DiscountIrr, FreightIrr = x.FreightIrr,
                        CommissionIrr = x.CommissionIrr, OtherCostsIrr = x.OtherCostsIrr
                    }).ToList()
                };
                var id = SalesService.SaveDraft(_db, draft, _userId);
                MessageBox.Show("فاکتور فروش با شناسه داخلی " + id + " در وضعیت بررسی ثبت شد.", "مازندشت",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshAll();
            }
            catch (Exception ex) { ShowError(ex, "ثبت فاکتور فروش"); }
        }

        // Bank -----------------------------------------------------------------
        private void CreateBankTransaction(object sender, RoutedEventArgs e)
        {
            try
            {
                long? statement = null;
                if (!String.IsNullOrWhiteSpace(_bankStatement.Text))
                    statement = Rials(_bankStatement.Text, "مانده پس از تراکنش");
                var draft = new BankTransactionDraft {
                    BankAccountCode = _bankAccount.SelectedValue as string,
                    Direction = _bankDirection.SelectedValue as string,
                    PartyAccountId = Optional(_bankParty.SelectedValue as string),
                    TransactionTypeId = Optional(_bankType.SelectedValue as string),
                    AmountIrr = Rials(_bankAmount.Text, "مبلغ"),
                    FeeIrr = Rials(_bankFee.Text, "کارمزد"),
                    StatementBalanceIrr = statement,
                    TrackingNumber = Optional(_bankTracking.Text),
                    DateIso = _bankDate.Text
                };
                var id = BankService.SaveDraft(_db, draft, _userId);
                MessageBox.Show("تراکنش بانکی با شناسه داخلی " + id + " در وضعیت بررسی ثبت شد.", "مازندشت",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshAll();
            }
            catch (Exception ex) { ShowError(ex, "ثبت تراکنش بانکی"); }
        }

        // Cheque -----------------------------------------------------------------
        private void CreateCheque(object sender, RoutedEventArgs e)
        {
            try
            {
                var draft = new ChequeDraft {
                    BankAccountCode = Optional(_chequeBank.SelectedValue as string),
                    Direction = _chequeDirection.SelectedValue as string,
                    PartyAccountId = Optional(_chequeBeneficiary.SelectedValue as string),
                    ChequeNumber = _chequeNumber.Text,
                    IssueDateIso = _chequeIssue.Text,
                    DueDateIso = _chequeDue.Text,
                    AmountIrr = Rials(_chequeAmount.Text, "مبلغ")
                };
                var id = ChequeService.SaveDraft(_db, draft, _userId);
                MessageBox.Show("چک با شناسه داخلی " + id + " در وضعیت بررسی ثبت شد.", "مازندشت",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshAll();
            }
            catch (Exception ex) { ShowError(ex, "ثبت چک"); }
        }

        private void ClearCheque(object sender, RoutedEventArgs e)
        {
            var row = _chequeList.SelectedItem as ChequeRow;
            if (row == null) { MessageBox.Show("یک چک انتخاب کنید.", "مازندشت"); return; }
            try
            {
                ChequeService.Clear(_db, row.Id, DateTime.Now.ToString("yyyy-MM-dd"), _userId);
                MessageBox.Show("چک وصول و تراکنش بانکی ساخته شد.", "مازندشت",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshAll();
            }
            catch (Exception ex) { ShowError(ex, "وصول چک"); }
        }

        private void MarkReturned(object sender, RoutedEventArgs e) { MarkCheque("RETURNED"); }

        private void MarkCancelled(object sender, RoutedEventArgs e) { MarkCheque("CANCELLED"); }

        private void MarkCheque(string status)
        {
            var row = _chequeList.SelectedItem as ChequeRow;
            if (row == null) { MessageBox.Show("یک چک انتخاب کنید.", "مازندشت"); return; }
            try
            {
                ChequeService.MarkStatus(_db, row.Id, status,
                    DateTime.Now.ToString("yyyy-MM-dd"), _userId);
                RefreshAll();
            }
            catch (Exception ex) { ShowError(ex, "تغییر وضعیت چک"); }
        }

        // Settlement -----------------------------------------------------------------
        private void AllocateSettlement(object sender, RoutedEventArgs e)
        {
            try
            {
                var draft = new SettlementDraft {
                    PaymentDocumentId = _settlePayment.SelectedValue as string,
                    InvoiceDocumentId = _settleInvoice.SelectedValue as string,
                    AmountIrr = Rials(_settleAmount.Text, "مبلغ تخصیص")
                };
                SettlementService.Allocate(_db, draft, _userId);
                MessageBox.Show("تخصیص ثبت شد.", "مازندشت", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshAll();
            }
            catch (Exception ex) { ShowError(ex, "ثبت تخصیص"); }
        }
    }
}
