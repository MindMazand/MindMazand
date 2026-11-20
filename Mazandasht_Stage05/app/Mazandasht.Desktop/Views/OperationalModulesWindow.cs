using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Mazandasht.Desktop.Services;

namespace Mazandasht.Desktop.Views
{
    /// <summary>
    /// Operational modules completed in G7: sorting, basket custody, partnership
    /// share split, and warehouse stock transfers. Every write goes through the
    /// matching service and lands as a REVIEW document with unposted movements.
    /// </summary>
    public sealed class OperationalModulesWindow : Window
    {
        private readonly string _db;
        private readonly string _userId;

        // Sorting
        private ComboBox _sortSourceWarehouse, _sortWarehouse, _sortProduct, _sortGrade, _sortPackaging;
        private TextBox _sortInput, _sortWaste, _sortLoss, _sortTolerance;
        private DataGrid _sortOutputs, _sortBatches;

        // Basket
        private ComboBox _basketType, _basketPackaging, _basketFrom, _basketTo, _basketFromWarehouse, _basketToWarehouse;
        private TextBox _basketQty, _basketPrice;
        private DataGrid _basketCustody, _basketMovements;

        // Partnership
        private ComboBox _splitDocument;
        private TextBox _splitNet;
        private DataGrid _splitShares, _splitResult;

        // Warehouse
        private ComboBox _transferLot;
        private ComboBox _transferFrom, _transferTo;
        private TextBox _transferQty;
        private DataGrid _stockOnHand;

        public OperationalModulesWindow(string databasePath, string userId)
        {
            _db = databasePath;
            _userId = userId;
            Title = "باسکول، انبار، سورتینگ، سبد و شراکت | مازندشت";
            Width = 1180; Height = 780;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FlowDirection = FlowDirection.RightToLeft;
            Background = ThemeService.Brush("WindowBackgroundBrush");
            Foreground = ThemeService.Brush("TextBrush");

            var root = new DockPanel { Margin = new Thickness(22) };
            var header = new TextBlock {
                Text = "عملیات باسکول، انبار، سورتینگ، سبد و شراکت",
                FontSize = 22, FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 16),
                Foreground = ThemeService.Brush("TextBrush")
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var tabs = new TabControl();
            tabs.Items.Add(BuildSortingTab());
            tabs.Items.Add(BuildBasketTab());
            tabs.Items.Add(BuildPartnershipTab());
            tabs.Items.Add(BuildWarehouseTab());
            root.Children.Add(tabs);

            Content = root;
            RefreshAll();
        }

        // ------------------------------------------------------------------
        private TabItem BuildSortingTab()
        {
            var panel = NewPanel();
            panel.Children.Add(Note("ورودی سورتینگ به‌صورت FIFO از موجودی تأییدشده انبار مبدأ تخصیص می‌یابد. سند در وضعیت «در بررسی» ثبت می‌شود و تا تأیید مدیر هیچ موجودی‌ای تغییر نمی‌کند."));

            var form = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _sortSourceWarehouse = Combo(form, "انبار مبدأ", MasterDataService.Warehouses(_db), false);
            _sortWarehouse = Combo(form, "انبار مقصد", MasterDataService.Warehouses(_db), false);
            _sortProduct = Combo(form, "محصول خروجی", MasterDataService.Products(_db), false);
            _sortGrade = CatalogCombo(form, "درجه", new List<CatalogRow>(), true);
            _sortPackaging = Combo(form, "بسته‌بندی", MasterDataService.Packaging(_db), true);
            _sortInput = Field(form, "وزن ورودی (گرم)", 130);
            _sortWaste = Field(form, "ضایات (گرم)", 120);
            _sortLoss = Field(form, "افت فرآیند (گرم)", 140);
            _sortTolerance = Field(form, "تلورانس (گرم)", 120);
            form.Children.Add(ActionButton("＋ ثبت سورتینگ", CreateSorting));
            panel.Children.Add(form);

            panel.Children.Add(new TextBlock { Text = "خروجی‌های درجه‌بندی", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 6, 0, 4) });
            _sortOutputs = EditableGrid();
            panel.Children.Add(_sortOutputs);
            panel.Children.Add(ActionButton("↳ افزودن خروجی", AddSortingOutput));

            panel.Children.Add(new TextBlock { Text = "سورتینگ‌های ثبت‌شده", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 10, 0, 4) });
            _sortBatches = Grid();
            panel.Children.Add(_sortBatches);
            _sortProduct.SelectionChanged += (s, e) => RefreshGrades();
            return new TabItem { Header = "سورتینگ و انبار", Content = panel };
        }

        private TabItem BuildBasketTab()
        {
            var panel = NewPanel();
            panel.Children.Add(Note("گردش سبد مستقل از پول است و هیچ المجو ایجاد نمی‌کند. مانده امانی هر طرف = ورودی امانی − خروجی امانی. برگشت از امانی نباید از مانده تأییدشده طرف بیشتر باشد."));

            var form = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _basketType = Combo(form, "نوع عملیات", new List<LookupItem> {
                new LookupItem { Id = "PURCHASE", Name = "خرید سبد" },
                new LookupItem { Id = "ISSUE", Name = "صدور سبد" },
                new LookupItem { Id = "RETURN", Name = "برگشت سبد" },
                new LookupItem { Id = "CUSTODY_IN", Name = "تحویل امانی" },
                new LookupItem { Id = "CUSTODY_OUT", Name = "برگشت امانی" },
                new LookupItem { Id = "TRANSFER_IN", Name = "انتقال ورود" },
                new LookupItem { Id = "TRANSFER_OUT", Name = "انتقال خروج" },
                new LookupItem { Id = "ADJUSTMENT", Name = "اصلاح" }
            }, false);
            _basketPackaging = Combo(form, "نوع سبد", MasterDataService.Packaging(_db), false);
            _basketFrom = Combo(form, "طرف مبدأ", MasterDataService.Parties(_db), true);
            _basketTo = Combo(form, "طرف مقصد", MasterDataService.Parties(_db), true);
            _basketFromWarehouse = Combo(form, "انبار مبدأ", MasterDataService.Warehouses(_db), true);
            _basketToWarehouse = Combo(form, "انبار مقصد", MasterDataService.Warehouses(_db), true);
            _basketQty = Field(form, "تعداد", 100);
            _basketPrice = Field(form, "قیمت واحد (ریال)", 140);
            form.Children.Add(ActionButton("＋ ثبت گردش سبد", CreateBasketMovement));
            panel.Children.Add(form);

            panel.Children.Add(new TextBlock { Text = "مانده امانی طرف‌ها", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 8, 0, 4) });
            _basketCustody = Grid();
            panel.Children.Add(_basketCustody);

            panel.Children.Add(new TextBlock { Text = "گردش‌های ثبت‌شده", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 10, 0, 4) });
            _basketMovements = Grid();
            panel.Children.Add(_basketMovements);
            return new TabItem { Header = "سبد و امانی", Content = panel };
        }

        private TabItem BuildPartnershipTab()
        {
            var panel = NewPanel();
            panel.Children.Add(Note("سهم شرکا باید دقیقاً به ۱۰۰ درصد برسد. واحد گردگیری ۱ کیلوگرم است و باقی‌مانده به آخرین ردیف داده می‌شود. تخصیص فقط روی سند پیش‌نویس یا در بررسی انجام می‌شود."));

            var form = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _splitDocument = Combo(form, "سند باسکول شراکت", new List<LookupItem>(), false);
            _splitNet = Field(form, "وزن خالص (گرم)", 130);
            form.Children.Add(ActionButton("محاسبهٔ سهم‌ها", ComputeSplit));
            form.Children.Add(ActionButton("ثبت سهم‌ها روی سند", ApplySplit));
            panel.Children.Add(form);

            panel.Children.Add(new TextBlock { Text = "سهم شرکا", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 6, 0, 4) });
            _splitShares = EditableGrid();
            panel.Children.Add(_splitShares);
            panel.Children.Add(ActionButton("↳ افزودن شریک", AddPartner));
            panel.Children.Add(ActionButton("↳ افزودن شریک از فهرست", AddPartnerFromCatalog));

            panel.Children.Add(new TextBlock { Text = "نتیجهٔ تخصیص", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 10, 0, 4) });
            _splitResult = Grid();
            panel.Children.Add(_splitResult);
            return new TabItem { Header = "شراکت", Content = panel };
        }

        private TabItem BuildWarehouseTab()
        {
            var panel = NewPanel();
            panel.Children.Add(Note("انتقال انبار به‌صورت جفت ثبت می‌شود: خروج از انبار مبدأ و ورود به انبار مقصد روی یک سند. تا تأیید مدیر، موجودی تغییر نمی‌کند."));

            var form = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _transferLot = Combo(form, "خط موجودی", new List<LookupItem>(), false);
            _transferFrom = Combo(form, "انبار مبدأ", MasterDataService.Warehouses(_db), false);
            _transferTo = Combo(form, "انبار مقصد", MasterDataService.Warehouses(_db), false);
            _transferQty = Field(form, "وزن انتقال (گرم)", 130);
            form.Children.Add(ActionButton("＋ ثبت انتقال", CreateTransfer));
            panel.Children.Add(form);

            panel.Children.Add(new TextBlock { Text = "موجودی تأییدشده به تفکیک خط و انبار", FontWeight = FontWeights.Bold,
                Foreground = ThemeService.Brush("TextBrush"), Margin = new Thickness(0, 8, 0, 4) });
            _stockOnHand = Grid();
            panel.Children.Add(_stockOnHand);
            return new TabItem { Header = "انبار و انتقال", Content = panel };
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

        private ComboBox Combo(Panel parent, string label, IEnumerable<LookupItem> items, bool optional)
        {
            var list = new List<LookupItem>(items);
            if (optional) list.Insert(0, new LookupItem { Id = "", Name = "—" });
            var combo = new ComboBox { Width = 210, Height = 34, Margin = new Thickness(5),
                DisplayMemberPath = "Display", SelectedValuePath = "Id", ItemsSource = list,
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"), ToolTip = label };
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
            parent.Children.Add(combo);
            return combo;
        }

        /// <summary>
        /// Catalog rows expose DisplayName rather than Display, so they need their
        /// own display member.
        /// </summary>
        private ComboBox CatalogCombo(Panel parent, string label, IEnumerable<CatalogRow> items,
            bool optional)
        {
            var list = new List<CatalogRow>(items);
            if (optional) list.Insert(0, new CatalogRow { Id = "", DisplayName = "—" });
            var combo = new ComboBox { Width = 210, Height = 34, Margin = new Thickness(5),
                DisplayMemberPath = "DisplayName", SelectedValuePath = "Id", ItemsSource = list,
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

        /// <summary>
        /// Editable grid used for the rows the operator must fill in (sorting
        /// outputs and partnership shares).
        /// </summary>
        private DataGrid EditableGrid()
        {
            return new DataGrid { AutoGenerateColumns = true, IsReadOnly = false,
                CanUserAddRows = false, Margin = new Thickness(0, 8, 0, 0),
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal };
        }

        // ------------------------------------------------------------------
        private void RefreshAll()
        {
            _sortBatches.ItemsSource = SortingService.List(_db);
            _basketCustody.ItemsSource = BasketService.CustodyBalances(_db);
            _basketMovements.ItemsSource = BasketService.Movements(_db);
            _stockOnHand.ItemsSource = WarehouseService.OnHand(_db);
            RefreshGrades();
            RefreshPartnershipDocuments();
            RefreshLots();
        }

        private void RefreshGrades()
        {
            var productId = _sortProduct.SelectedValue as string;
            _sortGrade.ItemsSource = productId == null
                ? new List<CatalogRow>() : BaseDataService.ProductGrades(_db, productId);
            if (_sortGrade.Items.Count > 0) _sortGrade.SelectedIndex = 0;
        }

        private void RefreshPartnershipDocuments()
        {
            var documents = new List<LookupItem>();
            using (var connection = new System.Data.SQLite.SQLiteConnection(
                "Data Source=" + _db + ";Version=3;Pooling=True;"))
            {
                connection.Open();
                using (var command = new System.Data.SQLite.SQLiteCommand(
                    "SELECT d.id,d.document_number,COALESCE(t.net_weight_g,0) FROM documents d "
                    + "JOIN weigh_tickets t ON t.document_id=d.id "
                    + "WHERE d.document_type='WEIGHING' AND d.status IN ('DRAFT','REVIEW') "
                    + "ORDER BY d.created_at_utc DESC LIMIT 200;", connection))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        documents.Add(new LookupItem {
                            Id = reader.GetString(0),
                            Code = reader.GetString(1),
                            Name = reader.GetString(1) + " | خالص " + reader.GetInt64(2) + " گرم"
                        });
            }
            _splitDocument.ItemsSource = documents;
            if (documents.Count > 0) _splitDocument.SelectedIndex = 0;
        }

        private void RefreshLots()
        {
            var lots = new List<LookupItem>();
            using (var connection = new System.Data.SQLite.SQLiteConnection(
                "Data Source=" + _db + ";Version=3;Pooling=True;"))
            {
                connection.Open();
                using (var command = new System.Data.SQLite.SQLiteCommand(
                    "SELECT stock_lot_id,lot_code,warehouse_name FROM v_stock_on_hand "
                    + "ORDER BY lot_code;", connection))
                using (var reader = command.ExecuteReader())
                    while (reader.Read())
                        lots.Add(new LookupItem { Id = reader.GetString(0), Code = reader.GetString(1),
                            Name = reader.GetString(1) + " | " + reader.GetString(2) });
            }
            _transferLot.ItemsSource = lots;
            if (lots.Count > 0) _transferLot.SelectedIndex = 0;
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

        // Sorting -----------------------------------------------------------------
        private sealed class OutputRow
        {
            public string ProductId { get; set; }
            public string GradeId { get; set; }
            public string PackagingTypeId { get; set; }
            public int PackageCount { get; set; }
            public long QuantityGrams { get; set; }
        }

        private readonly List<OutputRow> _outputs = new List<OutputRow>();

        private void AddSortingOutput(object sender, RoutedEventArgs e)
        {
            _outputs.Add(new OutputRow {
                ProductId = _sortProduct.SelectedValue as string,
                GradeId = Optional(_sortGrade.SelectedValue as string),
                PackagingTypeId = Optional(_sortPackaging.SelectedValue as string),
                PackageCount = 0, QuantityGrams = 0
            });
            _sortOutputs.ItemsSource = null;
            _sortOutputs.ItemsSource = _outputs;
        }

        private void CreateSorting(object sender, RoutedEventArgs e)
        {
            try
            {
                var draft = new SortingDraft {
                    WarehouseId = _sortWarehouse.SelectedValue as string,
                    SourceWarehouseId = Optional(_sortSourceWarehouse.SelectedValue as string),
                    InputWeightGrams = Grams(_sortInput.Text, "وزن ورودی"),
                    WasteWeightGrams = Grams(_sortWaste.Text, "ضایات"),
                    ProcessLossWeightGrams = Grams(_sortLoss.Text, "افت فرآیند"),
                    VarianceToleranceGrams = Grams(_sortTolerance.Text, "تلورانس"),
                    Notes = null,
                    Outputs = _outputs.Select(x => new SortingOutputLine {
                        ProductId = x.ProductId, GradeId = x.GradeId, PackagingTypeId = x.PackagingTypeId,
                        PackageCount = x.PackageCount, QuantityGrams = x.QuantityGrams
                    }).ToList()
                };
                var id = SortingService.SaveDraft(_db, draft, _userId);
                MessageBox.Show("سورتینگ با شناسه داخلی " + id + " در وضعیت بررسی ثبت شد.", "مازندشت",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshAll();
            }
            catch (Exception ex) { ShowError(ex, "ثبت سورتینگ"); }
        }

        // Basket -----------------------------------------------------------------
        private void CreateBasketMovement(object sender, RoutedEventArgs e)
        {
            try
            {
                var draft = new BasketMovementDraft {
                    PackagingTypeId = _basketPackaging.SelectedValue as string,
                    MovementType = _basketType.SelectedValue as string,
                    Quantity = (int)Grams(_basketQty.Text, "تعداد"),
                    FromPartyAccountId = Optional(_basketFrom.SelectedValue as string),
                    ToPartyAccountId = Optional(_basketTo.SelectedValue as string),
                    FromWarehouseId = Optional(_basketFromWarehouse.SelectedValue as string),
                    ToWarehouseId = Optional(_basketToWarehouse.SelectedValue as string),
                    UnitPriceIrr = Grams(_basketPrice.Text, "قیمت واحد")
                };
                var id = BasketService.SaveDraft(_db, draft, _userId);
                MessageBox.Show("گردش سبد با شناسه داخلی " + id + " در وضعیت بررسی ثبت شد.", "مازندشت",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshAll();
            }
            catch (Exception ex) { ShowError(ex, "ثبت گردش سبد"); }
        }

        // Partnership -------------------------------------------------------------
        private readonly List<PartnershipShare> _shares = new List<PartnershipShare>();

        private void AddPartner(object sender, RoutedEventArgs e)
        {
            _shares.Add(new PartnershipShare { PartyAccountId = null, SharePercent = 0m });
            _splitShares.ItemsSource = null;
            _splitShares.ItemsSource = _shares;
        }

        private void AddPartnerFromCatalog(object sender, RoutedEventArgs e)
        {
            var first = MasterDataService.Parties(_db).FirstOrDefault();
            _shares.Add(new PartnershipShare {
                PartyAccountId = first == null ? null : first.Id,
                SharePercent = 0m
            });
            _splitShares.ItemsSource = null;
            _splitShares.ItemsSource = _shares;
        }

        private void ComputeSplit(object sender, RoutedEventArgs e)
        {
            try
            {
                var net = Grams(_splitNet.Text, "وزن خالص");
                var shares = _shares.Select(x => new PartnershipShare {
                    PartyAccountId = x.PartyAccountId, SharePercent = x.SharePercent
                }).ToList();
                _splitResult.ItemsSource = PartnershipService.ComputeSplit(net, shares);
            }
            catch (Exception ex) { ShowError(ex, "محاسبهٔ سهم"); }
        }

        private void ApplySplit(object sender, RoutedEventArgs e)
        {
            try
            {
                var documentId = _splitDocument.SelectedValue as string;
                if (String.IsNullOrWhiteSpace(documentId))
                    throw new ArgumentException("ابتدا سند باسکول شراکت را انتخاب کنید.");
                var rows = _splitResult.ItemsSource as IList<PartnershipSplitRow>;
                if (rows == null || rows.Count == 0)
                    throw new ArgumentException("ابتدا سهم‌ها را محاسبه کنید.");
                var count = PartnershipService.ApplySplit(_db, documentId, rows, _userId);
                MessageBox.Show(count + " خط سهم روی سند ثبت شد.", "مازندشت",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { ShowError(ex, "ثبت سهم‌ها"); }
        }

        // Warehouse ---------------------------------------------------------------
        private void CreateTransfer(object sender, RoutedEventArgs e)
        {
            try
            {
                var draft = new StockTransferDraft {
                    StockLotId = _transferLot.SelectedValue as string,
                    FromWarehouseId = _transferFrom.SelectedValue as string,
                    ToWarehouseId = _transferTo.SelectedValue as string,
                    QuantityGrams = Grams(_transferQty.Text, "وزن انتقال")
                };
                var id = WarehouseService.Transfer(_db, draft, _userId);
                MessageBox.Show("انتقال انبار با شناسه داخلی " + id + " در وضعیت بررسی ثبت شد.", "مازندشت",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshAll();
            }
            catch (Exception ex) { ShowError(ex, "ثبت انتقال انبار"); }
        }

        private void ShowError(Exception exception, string title)
        {
            MessageBox.Show(exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
