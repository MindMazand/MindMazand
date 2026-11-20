using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mazandasht.Desktop.Services;

namespace Mazandasht.Desktop.Views
{
    /// <summary>
    /// Additional base-data catalogs that the operational modules need:
    /// main/sub groups, transaction types, product grades and packaging links,
    /// work groups and employees, operational parameters, accounting natures and
    /// groups, and real bank accounts.
    ///
    /// Every write goes through <see cref="BaseDataService"/>, which enforces the
    /// base.manage permission, never rewrites a reference code and writes an
    /// audit row in the same transaction.
    /// </summary>
    public sealed class BaseDataWindow : Window
    {
        private readonly string _db;
        private readonly string _userId;

        private DataGrid _mainGroupsGrid, _subGroupsGrid, _txnTypesGrid, _gradesGrid, _packagingGrid,
            _workGroupsGrid, _employeesGrid, _parametersGrid, _naturesGrid, _subNaturesGrid,
            _accountingGroupsGrid, _auditGroupsGrid, _bankAccountsGrid;

        private ComboBox _subGroupMain, _gradeProduct, _packagingProduct, _packagingType,
            _employeeParty, _employeeWorkGroup, _subNatureNature;

        private TextBox _mainGroupCode, _mainGroupName, _mainGroupNotes;
        private TextBox _subGroupCode, _subGroupName, _subGroupLedger;
        private TextBox _txnCode, _txnName;
        private ComboBox _txnEffect;
        private TextBox _gradeCode, _gradeName, _gradeSort;
        private TextBox _workGroupCode, _workGroupName;
        private TextBox _employeeJob;
        private TextBox _parameterCode, _parameterName, _parameterValue, _parameterUnit, _parameterNotes;
        private ComboBox _parameterKind;
        private TextBox _natureCode, _natureName, _naturePrefix;
        private TextBox _subNatureName;
        private ComboBox _subNatureEffect;
        private TextBox _accountingGroupCode, _accountingGroupName;
        private TextBox _auditGroupCode, _auditGroupName;
        private TextBox _bankTitle, _bankName, _bankBranch, _bankNumber, _bankIban, _bankOwner, _bankNotes;

        public BaseDataWindow(string databasePath, string userId)
        {
            _db = databasePath;
            _userId = userId;
            Title = "فهرست‌های پایه تکمیلی | مازندشت";
            Width = 1180; Height = 760;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FlowDirection = FlowDirection.RightToLeft;
            Background = ThemeService.Brush("WindowBackgroundBrush");
            Foreground = ThemeService.Brush("TextBrush");

            var root = new DockPanel { Margin = new Thickness(22) };
            var header = new TextBlock {
                Text = "فهرست‌های پایه تکمیلی",
                FontSize = 24, FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 18),
                Foreground = ThemeService.Brush("TextBrush")
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            var tabs = new TabControl();
            tabs.Items.Add(BuildGroupsTab());
            tabs.Items.Add(BuildTransactionTypesTab());
            tabs.Items.Add(BuildProductCatalogTab());
            tabs.Items.Add(BuildWorkTab());
            tabs.Items.Add(BuildParametersTab());
            tabs.Items.Add(BuildBankAccountsTab());
            tabs.Items.Add(BuildAccountingNaturesTab());
            root.Children.Add(tabs);

            Content = root;
            RefreshAll();
        }

        // ------------------------------------------------------------------
        // Tabs
        // ------------------------------------------------------------------
        private TabItem BuildGroupsTab()
        {
            var panel = NewPanel();
            panel.Children.Add(Note("گروه‌های اصلی و زیرگروه‌ها محصولات و اشخاص را دسته‌بندی می‌کنند. کدها پس از تعریف تغییر نمی‌کنند."));

            var mainForm = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _mainGroupCode = Field(mainForm, "کد گروه اصلی", 150);
            _mainGroupName = Field(mainForm, "نام گروه اصلی", 220);
            _mainGroupNotes = Field(mainForm, "توضیحات", 200);
            mainForm.Children.Add(ActionButton("＋ تعریف گروه اصلی", CreateMainGroup));
            panel.Children.Add(mainForm);
            _mainGroupsGrid = Grid();
            panel.Children.Add(_mainGroupsGrid);

            var subForm = new WrapPanel { Margin = new Thickness(0, 14, 0, 12) };
            _subGroupMain = Combo(subForm, "گروه اصلی", BaseDataService.MainGroups(_db), false);
            _subGroupCode = Field(subForm, "کد زیرگروه", 150);
            _subGroupName = Field(subForm, "نام زیرگروه", 220);
            _subGroupLedger = Field(subForm, "کد حساب کنترلی (اختیاری)", 170);
            subForm.Children.Add(ActionButton("＋ تعریف زیرگروه", CreateSubGroup));
            panel.Children.Add(subForm);
            _subGroupsGrid = Grid();
            panel.Children.Add(_subGroupsGrid);
            return new TabItem { Header = "گروه‌های اصلی و زیرگروه", Content = panel };
        }

        private TabItem BuildTransactionTypesTab()
        {
            var panel = NewPanel();
            panel.Children.Add(Note("نوع تراکنش مشخص می‌کند مبلغ باید مانده را افزایش دهد، کاهش دهد یا خنثی باشد. برای تراز اسناد در گزارش‌ها استفاده می‌شود."));
            var form = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _txnCode = Field(form, "کد نوع تراکنش", 160);
            _txnName = Field(form, "عنوان", 240);
            _txnEffect = Combo(form, "اثر بر مانده", new List<LookupItem> {
                new LookupItem { Id = "INCREASE", Name = "افزایش مانده" },
                new LookupItem { Id = "DECREASE", Name = "کاهش مانده" },
                new LookupItem { Id = "NEUTRAL", Name = "خنثی" },
                new LookupItem { Id = "BY_DIRECTION", Name = "بر اساس جهت" },
                new LookupItem { Id = "CUSTOM", Name = "سفارشی" }
            }, false);
            form.Children.Add(ActionButton("＋ تعریف نوع تراکنش", CreateTransactionType));
            panel.Children.Add(form);
            _txnTypesGrid = Grid();
            panel.Children.Add(_txnTypesGrid);
            return new TabItem { Header = "انواع تراکنش", Content = panel };
        }

        private TabItem BuildProductCatalogTab()
        {
            var panel = NewPanel();
            panel.Children.Add(Note("درجه‌بندی محصولات (مثل درجه یک، درجه دو) و بسته‌بندی‌های قابل استفاده برای هر محصول در این زبانه تعریف می‌شود."));

            var gradeForm = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _gradeProduct = Combo(gradeForm, "محصول", MasterDataService.Products(_db), false);
            _gradeCode = Field(gradeForm, "کد درجه", 130);
            _gradeName = Field(gradeForm, "عنوان درجه", 200);
            _gradeSort = Field(gradeForm, "ترتیب نمایش", 110);
            gradeForm.Children.Add(ActionButton("＋ تعریف درجه", CreateGrade));
            panel.Children.Add(gradeForm);
            _gradesGrid = Grid();
            panel.Children.Add(_gradesGrid);

            var packagingForm = new WrapPanel { Margin = new Thickness(0, 14, 0, 12) };
            _packagingProduct = Combo(packagingForm, "محصول", MasterDataService.Products(_db), false);
            _packagingType = Combo(packagingForm, "بسته‌بندی", MasterDataService.Packaging(_db), false);
            packagingForm.Children.Add(ActionButton("＋ اتصال بسته‌بندی به‌عنوان پیش‌فرض", LinkPackaging));
            panel.Children.Add(packagingForm);
            _packagingGrid = Grid();
            panel.Children.Add(_packagingGrid);

            _gradeProduct.SelectionChanged += (s, e) => RefreshProductCatalog();
            _packagingProduct.SelectionChanged += (s, e) => RefreshProductCatalog();
            return new TabItem { Header = "درجه‌بندی و بسته‌بندی محصول", Content = panel };
        }

        private TabItem BuildWorkTab()
        {
            var panel = NewPanel();
            panel.Children.Add(Note("گروه کاری و کارکنان برای وابستگی اسناد به افراد و محاسبه دستمزد در فاز بعد استفاده می‌شود. کارمند باید ابتدا یک طرف حساب باشد."));
            var form = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _workGroupCode = Field(form, "کد گروه کاری", 150);
            _workGroupName = Field(form, "نام گروه کاری", 220);
            form.Children.Add(ActionButton("＋ تعریف گروه کاری", CreateWorkGroup));
            panel.Children.Add(form);
            _workGroupsGrid = Grid();
            panel.Children.Add(_workGroupsGrid);

            var employeeForm = new WrapPanel { Margin = new Thickness(0, 14, 0, 12) };
            _employeeParty = Combo(employeeForm, "طرف حساب (کارمند)", MasterDataService.Parties(_db), false);
            _employeeWorkGroup = Combo(employeeForm, "گروه کاری", BaseDataService.WorkGroups(_db), true);
            _employeeJob = Field(employeeForm, "عنوان شغلی", 200);
            employeeForm.Children.Add(ActionButton("＋ تعریف کارمند", CreateEmployee));
            panel.Children.Add(employeeForm);
            _employeesGrid = Grid();
            panel.Children.Add(_employeesGrid);
            return new TabItem { Header = "گروه‌های کاری و کارکنان", Content = panel };
        }

        private TabItem BuildParametersTab()
        {
            var panel = NewPanel();
            panel.Children.Add(Note("پارامترهای عملیاتی مانند قاعده سال مالی و نرخ مالیات در این زبانه تنظیم می‌شوند. تغییر این مقادیر قبل از ثبت اسناد واقعی باید با مالک سیستم هماهنگ باشد."));
            var form = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _parameterCode = Field(form, "کد پارامتر", 170);
            _parameterName = Field(form, "عنوان", 220);
            _parameterKind = Combo(form, "نوع مقدار", new List<LookupItem> {
                new LookupItem { Id = "TEXT", Name = "متن" },
                new LookupItem { Id = "INTEGER", Name = "عدد صحیح" },
                new LookupItem { Id = "BOOLEAN", Name = "بلی/خیر" }
            }, false);
            _parameterValue = Field(form, "مقدار", 180);
            _parameterUnit = Field(form, "واحد (اختیاری)", 120);
            _parameterNotes = Field(form, "توضیحات", 200);
            form.Children.Add(ActionButton("＋ ثبت پارامتر", SetParameter));
            panel.Children.Add(form);
            _parametersGrid = Grid();
            panel.Children.Add(_parametersGrid);
            return new TabItem { Header = "پارامترهای عملیاتی", Content = panel };
        }

        private TabItem BuildBankAccountsTab()
        {
            var panel = NewPanel();
            panel.Children.Add(Note("شماره حساب و شبا با رمزنگاری DPAPI ذخیره می‌شوند و در رابط کاربری هرگز به‌صورت متن ساده نمایش داده نمی‌شوند. برای ثبت حساب باید مجوز مدیریت اطلاعات پایه داشته باشید."));
            var form = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _bankTitle = Field(form, "عنوان حساب", 200);
            _bankName = Field(form, "نام بانک", 180);
            _bankBranch = Field(form, "شعبه", 150);
            _bankNumber = Field(form, "شماره حساب", 190);
            _bankIban = Field(form, "شماره شبا", 210);
            _bankOwner = Field(form, "نام صاحب حساب", 180);
            _bankNotes = Field(form, "توضیحات", 180);
            form.Children.Add(ActionButton("＋ ثبت حساب بانکی", CreateBankAccount));
            panel.Children.Add(form);
            _bankAccountsGrid = Grid();
            panel.Children.Add(_bankAccountsGrid);
            return new TabItem { Header = "حساب‌های بانکی", Content = panel };
        }

        private TabItem BuildAccountingNaturesTab()
        {
            var panel = NewPanel();
            panel.Children.Add(Note("ماهیت حساب‌ها و گروه‌های حسابداری و ممیزی زیربنای حسابداری دوبل در فاز ۹ هستند. تعریف آن‌ها به‌تنهایی ثبت مالی ایجاد نمی‌کند."));

            var natureForm = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            _natureCode = Field(natureForm, "کد ماهیت", 140);
            _natureName = Field(natureForm, "عنوان ماهیت", 220);
            _naturePrefix = Field(natureForm, "پیشوند کد (اختیاری)", 150);
            natureForm.Children.Add(ActionButton("＋ تعریف ماهیت حساب", CreateAccountNature));
            panel.Children.Add(natureForm);
            _naturesGrid = Grid();
            panel.Children.Add(_naturesGrid);

            var subNatureForm = new WrapPanel { Margin = new Thickness(0, 14, 0, 12) };
            _subNatureNature = Combo(subNatureForm, "ماهیت حساب", BaseDataService.AccountNatures(_db), false);
            _subNatureName = Field(subNatureForm, "عنوان زیرماهیت", 220);
            _subNatureEffect = Combo(subNatureForm, "اثر حسابداری", new List<LookupItem> {
                new LookupItem { Id = "ASSET", Name = "دارایی" },
                new LookupItem { Id = "LIABILITY", Name = "بدهی" },
                new LookupItem { Id = "EQUITY", Name = "سرمایه" },
                new LookupItem { Id = "REVENUE", Name = "درآمد" },
                new LookupItem { Id = "EXPENSE", Name = "هزینه" },
                new LookupItem { Id = "CONTROL", Name = "کنترلی" }
            }, false);
            subNatureForm.Children.Add(ActionButton("＋ تعریف زیرماهیت", CreateAccountSubNature));
            panel.Children.Add(subNatureForm);
            _subNaturesGrid = Grid();
            panel.Children.Add(_subNaturesGrid);
            _subNatureNature.SelectionChanged += (s, e) => RefreshNatures();

            var groupsForm = new WrapPanel { Margin = new Thickness(0, 14, 0, 12) };
            _accountingGroupCode = Field(groupsForm, "کد گروه حسابداری", 150);
            _accountingGroupName = Field(groupsForm, "عنوان گروه حسابداری", 200);
            groupsForm.Children.Add(ActionButton("＋ گروه حسابداری", CreateAccountingGroup));
            _auditGroupCode = Field(groupsForm, "کد گروه ممیزی", 150);
            _auditGroupName = Field(groupsForm, "عنوان گروه ممیزی", 200);
            groupsForm.Children.Add(ActionButton("＋ گروه ممیزی", CreateAuditGroup));
            panel.Children.Add(groupsForm);
            _accountingGroupsGrid = Grid();
            panel.Children.Add(_accountingGroupsGrid);
            _auditGroupsGrid = Grid();
            panel.Children.Add(_auditGroupsGrid);
            return new TabItem { Header = "ماهیت حساب‌ها و گروه‌ها", Content = panel };
        }

        // ------------------------------------------------------------------
        // Builders
        // ------------------------------------------------------------------
        private StackPanel NewPanel()
        {
            return new StackPanel { Margin = new Thickness(14) };
        }

        private TextBlock Note(string text)
        {
            return new TextBlock {
                Text = text, TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeService.Brush("MutedTextBrush"),
                Margin = new Thickness(4, 0, 4, 10)
            };
        }

        private TextBox Field(Panel parent, string label, double width)
        {
            var box = new TextBox {
                Width = width, Height = 34, Margin = new Thickness(5),
                VerticalContentAlignment = VerticalAlignment.Center, ToolTip = label,
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                BorderBrush = ThemeService.Brush("GlassBorderBrush")
            };
            parent.Children.Add(box);
            return box;
        }

        private ComboBox Combo(Panel parent, string label, IEnumerable<LookupItem> items, bool optional)
        {
            var list = new List<LookupItem>(items);
            if (optional) list.Insert(0, new LookupItem { Id = "", Name = "—" });
            var combo = new ComboBox {
                Width = 210, Height = 34, Margin = new Thickness(5),
                DisplayMemberPath = "Display", SelectedValuePath = "Id", ItemsSource = list,
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                ToolTip = label
            };
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
            parent.Children.Add(combo);
            return combo;
        }

        private Button ActionButton(string text, RoutedEventHandler click)
        {
            var button = new Button {
                Content = text, Height = 36, Margin = new Thickness(5),
                Padding = new Thickness(14, 5, 14, 5),
                Background = ThemeService.Brush("AccentBrush"),
                Foreground = Brushes.White, BorderThickness = new Thickness(0)
            };
            button.Click += click;
            return button;
        }

        private DataGrid Grid()
        {
            return new DataGrid {
                AutoGenerateColumns = true, IsReadOnly = true, CanUserAddRows = false,
                Margin = new Thickness(0, 8, 0, 0),
                Background = ThemeService.Brush("PanelBackgroundBrush"),
                Foreground = ThemeService.Brush("TextBrush"),
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
            };
        }

        // ------------------------------------------------------------------
        // Refresh
        // ------------------------------------------------------------------
        private void RefreshAll()
        {
            _mainGroupsGrid.ItemsSource = BaseDataService.MainGroups(_db);
            _subGroupsGrid.ItemsSource = BaseDataService.SubGroups(_db);
            _txnTypesGrid.ItemsSource = BaseDataService.TransactionTypes(_db);
            _workGroupsGrid.ItemsSource = BaseDataService.WorkGroups(_db);
            _employeesGrid.ItemsSource = BaseDataService.Employees(_db);
            _parametersGrid.ItemsSource = BaseDataService.Parameters(_db);
            _naturesGrid.ItemsSource = BaseDataService.AccountNatures(_db);
            _accountingGroupsGrid.ItemsSource = BaseDataService.AccountingGroups(_db);
            _auditGroupsGrid.ItemsSource = BaseDataService.AuditGroups(_db);
            _bankAccountsGrid.ItemsSource = BaseDataService.BankAccounts(_db);

            _subGroupMain.ItemsSource = BaseDataService.MainGroups(_db);
            if (_subGroupMain.Items.Count > 0) _subGroupMain.SelectedIndex = 0;
            _employeeWorkGroup.ItemsSource = BaseDataService.WorkGroups(_db);
            if (_employeeWorkGroup.Items.Count > 1) _employeeWorkGroup.SelectedIndex = 1;
            _employeeParty.ItemsSource = MasterDataService.Parties(_db);
            if (_employeeParty.Items.Count > 0) _employeeParty.SelectedIndex = 0;

            RefreshProductCatalog();
            RefreshNatures();
        }

        private void RefreshProductCatalog()
        {
            var productId = _gradeProduct.SelectedValue as string;
            _gradesGrid.ItemsSource = productId == null
                ? new List<CatalogRow>() : BaseDataService.ProductGrades(_db, productId);
            var packagingProductId = _packagingProduct.SelectedValue as string;
            _packagingGrid.ItemsSource = packagingProductId == null
                ? new List<CatalogRow>() : BaseDataService.ProductPackaging(_db, packagingProductId);
            _packagingType.ItemsSource = MasterDataService.Packaging(_db);
            if (_packagingType.Items.Count > 0) _packagingType.SelectedIndex = 0;
        }

        private void RefreshNatures()
        {
            var natureId = _subNatureNature.SelectedValue as string;
            _subNaturesGrid.ItemsSource = natureId == null
                ? new List<CatalogRow>() : BaseDataService.AccountSubNatures(_db, natureId);
        }

        // ------------------------------------------------------------------
        // Actions
        // ------------------------------------------------------------------
        private void CreateMainGroup(object sender, RoutedEventArgs e)
        {
            try
            {
                BaseDataService.CreateMainGroup(_db, _mainGroupCode.Text, _mainGroupName.Text,
                    _mainGroupNotes.Text, _userId);
                _mainGroupCode.Clear(); _mainGroupName.Clear(); _mainGroupNotes.Clear();
                RefreshAll();
            }
            catch (Exception ex) { ShowError(ex, "تعریف گروه اصلی"); }
        }

        private void CreateSubGroup(object sender, RoutedEventArgs e)
        {
            try
            {
                BaseDataService.CreateSubGroup(_db, _subGroupCode.Text,
                    _subGroupMain.SelectedValue as string, _subGroupName.Text,
                    Optional(_subGroupLedger.Text), null, _userId);
                _subGroupCode.Clear(); _subGroupName.Clear(); _subGroupLedger.Clear();
                RefreshAll();
            }
            catch (Exception ex) { ShowError(ex, "تعریف زیرگروه"); }
        }

        private void CreateTransactionType(object sender, RoutedEventArgs e)
        {
            try
            {
                BaseDataService.CreateTransactionType(_db, _txnCode.Text, _txnName.Text,
                    _txnEffect.SelectedValue as string, null, _userId);
                _txnCode.Clear(); _txnName.Clear();
                RefreshAll();
            }
            catch (Exception ex) { ShowError(ex, "تعریف نوع تراکنش"); }
        }

        private void CreateGrade(object sender, RoutedEventArgs e)
        {
            try
            {
                int sort = 0;
                if (!String.IsNullOrWhiteSpace(_gradeSort.Text) && !Int32.TryParse(_gradeSort.Text, out sort))
                    throw new ArgumentException("ترتیب نمایش باید عدد صحیح باشد.");
                BaseDataService.CreateProductGrade(_db, _gradeProduct.SelectedValue as string,
                    _gradeCode.Text, _gradeName.Text, sort, _userId);
                _gradeCode.Clear(); _gradeName.Clear(); _gradeSort.Clear();
                RefreshProductCatalog();
            }
            catch (Exception ex) { ShowError(ex, "تعریف درجه محصول"); }
        }

        private void LinkPackaging(object sender, RoutedEventArgs e)
        {
            try
            {
                BaseDataService.SetProductPackaging(_db, _packagingProduct.SelectedValue as string,
                    _packagingType.SelectedValue as string, true, _userId);
                RefreshProductCatalog();
            }
            catch (Exception ex) { ShowError(ex, "اتصال بسته‌بندی"); }
        }

        private void CreateWorkGroup(object sender, RoutedEventArgs e)
        {
            try
            {
                BaseDataService.CreateWorkGroup(_db, _workGroupCode.Text, _workGroupName.Text, null, _userId);
                _workGroupCode.Clear(); _workGroupName.Clear();
                RefreshAll();
            }
            catch (Exception ex) { ShowError(ex, "تعریف گروه کاری"); }
        }

        private void CreateEmployee(object sender, RoutedEventArgs e)
        {
            try
            {
                BaseDataService.CreateEmployee(_db, _employeeParty.SelectedValue as string,
                    Optional(_employeeWorkGroup.SelectedValue as string), _employeeJob.Text, null, _userId);
                _employeeJob.Clear();
                RefreshAll();
            }
            catch (Exception ex) { ShowError(ex, "تعریف کارمند"); }
        }

        private void SetParameter(object sender, RoutedEventArgs e)
        {
            try
            {
                BaseDataService.SetParameter(_db, _parameterCode.Text, _parameterName.Text,
                    _parameterValue.Text, _parameterKind.SelectedValue as string,
                    _parameterUnit.Text, null, _parameterNotes.Text, _userId);
                _parameterCode.Clear(); _parameterName.Clear(); _parameterValue.Clear();
                _parameterUnit.Clear(); _parameterNotes.Clear();
                RefreshAll();
            }
            catch (Exception ex) { ShowError(ex, "ثبت پارامتر"); }
        }

        private void CreateBankAccount(object sender, RoutedEventArgs e)
        {
            try
            {
                BaseDataService.CreateBankAccount(_db, _bankTitle.Text, _bankName.Text,
                    _bankBranch.Text, _bankNumber.Text, _bankIban.Text, _bankOwner.Text,
                    _bankNotes.Text, _userId);
                _bankTitle.Clear(); _bankName.Clear(); _bankBranch.Clear();
                _bankNumber.Clear(); _bankIban.Clear(); _bankOwner.Clear(); _bankNotes.Clear();
                RefreshAll();
            }
            catch (Exception ex) { ShowError(ex, "ثبت حساب بانکی"); }
        }

        private void CreateAccountNature(object sender, RoutedEventArgs e)
        {
            try
            {
                BaseDataService.CreateAccountNature(_db, _natureCode.Text, _natureName.Text,
                    _naturePrefix.Text, _userId);
                _natureCode.Clear(); _natureName.Clear(); _naturePrefix.Clear();
                RefreshAll();
            }
            catch (Exception ex) { ShowError(ex, "تعریف ماهیت حساب"); }
        }

        private void CreateAccountSubNature(object sender, RoutedEventArgs e)
        {
            try
            {
                BaseDataService.CreateAccountSubNature(_db, _subNatureNature.SelectedValue as string,
                    _subNatureName.Text, _subNatureEffect.SelectedValue as string, _userId);
                _subNatureName.Clear();
                RefreshNatures();
            }
            catch (Exception ex) { ShowError(ex, "تعریف زیرماهیت"); }
        }

        private void CreateAccountingGroup(object sender, RoutedEventArgs e)
        {
            try
            {
                BaseDataService.CreateAccountingGroup(_db, _accountingGroupCode.Text,
                    _accountingGroupName.Text, _userId);
                _accountingGroupCode.Clear(); _accountingGroupName.Clear();
                RefreshAll();
            }
            catch (Exception ex) { ShowError(ex, "تعریف گروه حسابداری"); }
        }

        private void CreateAuditGroup(object sender, RoutedEventArgs e)
        {
            try
            {
                BaseDataService.CreateAuditGroup(_db, _auditGroupCode.Text, _auditGroupName.Text, _userId);
                _auditGroupCode.Clear(); _auditGroupName.Clear();
                RefreshAll();
            }
            catch (Exception ex) { ShowError(ex, "تعریف گروه ممیزی"); }
        }

        private void ShowError(Exception exception, string title)
        {
            MessageBox.Show(exception.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private static string Optional(string value)
        {
            return String.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
