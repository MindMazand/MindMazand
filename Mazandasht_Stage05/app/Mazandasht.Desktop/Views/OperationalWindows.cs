using System;
using System.Linq;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mazandasht.Desktop.Services;

namespace Mazandasht.Desktop.Views
{
    public sealed class MasterDataWindow : Window
    {
        private readonly string _db;
        private readonly string _userId;
        private DataGrid _partiesGrid, _productsGrid, _packagingGrid, _warehousesGrid, _banksGrid, _operationsGrid;
        private TextBox _bankCode, _bankName, _operationCode, _operationName;
        private ComboBox _operationDirection, _operationGroup;
        private TextBox _partyName, _partyNationalId, _partyPhone, _partyAddress;
        private TextBox _productName, _groupName, _packName, _tare, _warehouseName, _warehouseLocation;
        private ComboBox _productGroup, _productUnit, _packKind;

        public MasterDataWindow(string databasePath, string userId)
        {
            _db = databasePath; _userId = userId;
            Title = "اطلاعات پایه | مازندشت"; Width = 1120; Height = 720;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            FlowDirection = FlowDirection.RightToLeft;
            Background = new SolidColorBrush(Color.FromRgb(11,23,17));
            Foreground = Brushes.White;
            var root = new DockPanel { Margin = new Thickness(22) };
            var header = new TextBlock { Text = "مدیریت اطلاعات پایه", FontSize = 24, FontWeight = FontWeights.Bold, Margin = new Thickness(0,0,0,18) };
            DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
            var tabs = new TabControl();
            tabs.Items.Add(BuildPartiesTab());
            tabs.Items.Add(BuildProductsTab());
            tabs.Items.Add(BuildPackagingTab());
            tabs.Items.Add(BuildWarehousesTab());
            tabs.Items.Add(BuildBanksTab());
            tabs.Items.Add(BuildOperationsTab());
            root.Children.Add(tabs);
            Content = root;
            RefreshAll();
        }

        private TabItem BuildPartiesTab()
        {
            var panel = NewPanel();
            var form = new WrapPanel { Margin = new Thickness(0,0,0,12) };
            _partyName = Field(form, "نام طرف حساب", 180);
            _partyNationalId = Field(form, "کد ملی", 130);
            _partyPhone = Field(form, "شماره تماس", 140);
            _partyAddress = Field(form, "آدرس", 220);
            form.Children.Add(ActionButton("＋ ثبت سریع شخص", CreateParty));
            panel.Children.Add(form);
            _partiesGrid = Grid(); panel.Children.Add(_partiesGrid);
            return new TabItem { Header="اشخاص و طرف حساب‌ها", Content=panel };
        }

        private TabItem BuildProductsTab()
        {
            var panel = NewPanel();
            var form = new WrapPanel { Margin = new Thickness(0,0,0,10) };
            _groupName = Field(form, "گروه جدید", 180);
            form.Children.Add(ActionButton("＋ تعریف گروه", CreateGroup));
            _productGroup = new ComboBox { Width=190, Height=34, Margin=new Thickness(5), DisplayMemberPath="Display", SelectedValuePath="Id" };
            form.Children.Add(_productGroup);
            _productName = Field(form, "نام محصول", 180);
            _productUnit = new ComboBox { Width=140, Height=34, Margin=new Thickness(5), DisplayMemberPath="Display", SelectedValuePath="Id" };
            form.Children.Add(_productUnit);
            form.Children.Add(ActionButton("＋ ثبت محصول", CreateProduct));
            panel.Children.Add(form);
            _productsGrid = Grid(); panel.Children.Add(_productsGrid);
            return new TabItem { Header="گروه‌ها و محصولات", Content=panel };
        }

        private TabItem BuildPackagingTab()
        {
            var panel = NewPanel();
            var form = new WrapPanel { Margin = new Thickness(0,0,0,10) };
            _packName = Field(form, "نام بسته‌بندی", 190);
            _packKind = new ComboBox { Width=150, Height=34, Margin=new Thickness(5) };
            _packKind.Items.Add(new ComboBoxItem { Content="سبد", Tag="BASKET" });
            _packKind.Items.Add(new ComboBoxItem { Content="کارتن", Tag="CARTON" });
            _packKind.Items.Add(new ComboBoxItem { Content="کیسه", Tag="BAG" });
            _packKind.Items.Add(new ComboBoxItem { Content="فله", Tag="BULK" });
            _packKind.Items.Add(new ComboBoxItem { Content="سایر", Tag="OTHER" });
            _packKind.SelectedIndex=0; form.Children.Add(_packKind);
            _tare = Field(form, "وزن ظرف (گرم)", 120);
            form.Children.Add(ActionButton("＋ ثبت بسته‌بندی", CreatePackaging));
            panel.Children.Add(form);
            _packagingGrid = Grid(); panel.Children.Add(_packagingGrid);
            return new TabItem { Header="بسته‌بندی و سبد", Content=panel };
        }

        private TabItem BuildWarehousesTab()
        {
            var panel=NewPanel();
            var form=new WrapPanel { Margin=new Thickness(0,0,0,10) };
            _warehouseName=Field(form,"نام انبار",220);
            _warehouseLocation=Field(form,"موقعیت",220);
            form.Children.Add(ActionButton("＋ ثبت انبار",CreateWarehouse));
            panel.Children.Add(form);
            _warehousesGrid=Grid(); panel.Children.Add(_warehousesGrid);
            return new TabItem { Header="انبارها", Content=panel };
        }

        private TabItem BuildBanksTab()
        {
            var panel = NewPanel();
            panel.Children.Add(new TextBlock {
                Text="فهرست نمونه بانک‌ها فقط نام مؤسسات را نگهداری می‌کند؛ حساب بانکی، شماره حساب، شبا و مانده باید جداگانه و توسط کاربر تعریف شوند.",
                TextWrapping=TextWrapping.Wrap, Foreground=new SolidColorBrush(Color.FromRgb(169,185,174)),
                Margin=new Thickness(5,0,5,10)
            });
            var form = new WrapPanel { Margin=new Thickness(0,0,0,10) };
            _bankCode = Field(form,"کد یکتای بانک (انگلیسی)",180);
            _bankName = Field(form,"نام بانک",240);
            form.Children.Add(ActionButton("＋ افزودن بانک",CreateBankCatalogEntry));
            panel.Children.Add(form);
            _banksGrid = Grid();
            panel.Children.Add(_banksGrid);
            return new TabItem { Header="فهرست بانک‌ها", Content=panel };
        }

        private TabItem BuildOperationsTab()
        {
            var panel = NewPanel();
            panel.Children.Add(new TextBlock {
                Text="عملیات پیش‌فرض برای شناخت ساختار سیستم نمایش داده می‌شوند. مدیر می‌تواند عملیات جدید با ماهیت ورود، خروج یا خنثی تعریف کند؛ تعریف نوع عملیات به‌تنهایی سند یا گردش ایجاد نمی‌کند.",
                TextWrapping=TextWrapping.Wrap, Foreground=new SolidColorBrush(Color.FromRgb(169,185,174)),
                Margin=new Thickness(5,0,5,10)
            });
            var form = new WrapPanel { Margin=new Thickness(0,0,0,10) };
            _operationCode = Field(form,"کد عملیات (انگلیسی)",180);
            _operationName = Field(form,"عنوان عملیات",220);
            _operationDirection = new ComboBox { Width=150, Height=34, Margin=new Thickness(5) };
            _operationDirection.Items.Add(new ComboBoxItem { Content="ورودی", Tag="IN" });
            _operationDirection.Items.Add(new ComboBoxItem { Content="خروجی", Tag="OUT" });
            _operationDirection.Items.Add(new ComboBoxItem { Content="خنثی", Tag="NEUTRAL" });
            _operationDirection.SelectedIndex=2;
            form.Children.Add(_operationDirection);
            _operationGroup = new ComboBox { Width=210, Height=34, Margin=new Thickness(5),
                DisplayMemberPath="Display", SelectedValuePath="Id" };
            form.Children.Add(_operationGroup);
            form.Children.Add(ActionButton("＋ افزودن نوع عملیات",CreateOperationCatalogEntry));
            panel.Children.Add(form);
            _operationsGrid = Grid();
            panel.Children.Add(_operationsGrid);
            return new TabItem { Header="انواع عملیات", Content=panel };
        }

        private StackPanel NewPanel()
        {
            return new StackPanel { Margin=new Thickness(14) };
        }
        private TextBox Field(Panel parent, string label, double width)
        {
            var box = new TextBox { Width=width, Height=34, Margin=new Thickness(5), VerticalContentAlignment=VerticalAlignment.Center,
                ToolTip=label, Background=new SolidColorBrush(Color.FromRgb(29,48,38)), Foreground=Brushes.White,
                BorderBrush=new SolidColorBrush(Color.FromRgb(90,125,100)) };
            parent.Children.Add(box); return box;
        }
        private Button ActionButton(string text, RoutedEventHandler click)
        {
            var b = new Button { Content=text, Height=36, Margin=new Thickness(5), Padding=new Thickness(14,5,14,5),
                Background=new SolidColorBrush(Color.FromRgb(212,105,9)), Foreground=Brushes.White, BorderThickness=new Thickness(0) };
            b.Click += click; return b;
        }
        private DataGrid Grid()
        {
            return new DataGrid { AutoGenerateColumns=true, IsReadOnly=true, CanUserAddRows=false, Margin=new Thickness(0,8,0,0),
                Background=new SolidColorBrush(Color.FromRgb(23,39,30)), Foreground=Brushes.White, GridLinesVisibility=DataGridGridLinesVisibility.Horizontal };
        }
        private void RefreshAll()
        {
            _partiesGrid.ItemsSource = MasterDataService.Parties(_db);
            _productsGrid.ItemsSource = MasterDataService.Products(_db);
            _packagingGrid.ItemsSource = MasterDataService.Packaging(_db);
            _warehousesGrid.ItemsSource = MasterDataService.Warehouses(_db);
            _productGroup.ItemsSource = MasterDataService.ProductGroups(_db);
            _productUnit.ItemsSource = MasterDataService.Units(_db);
            if (_productUnit.Items.Count > 0 && _productUnit.SelectedIndex < 0) _productUnit.SelectedIndex=0;
            if (_banksGrid != null) _banksGrid.ItemsSource = SystemCatalogService.GetBanks(_db);
            if (_operationsGrid != null) _operationsGrid.ItemsSource = SystemCatalogService.GetOperations(_db);
            if (_operationGroup != null)
            {
                _operationGroup.ItemsSource = SystemCatalogService.GetMainGroups(_db);
                if (_operationGroup.Items.Count > 0 && _operationGroup.SelectedIndex < 0) _operationGroup.SelectedIndex=0;
            }
        }
        private static string NormalizeDigits(string value)
        {
            if(String.IsNullOrEmpty(value)) return value;
            var chars=value.ToCharArray();
            for(int i=0;i<chars.Length;i++)
            {
                if(chars[i]>='۰' && chars[i]<='۹') chars[i]=(char)('0'+(chars[i]-'۰'));
                else if(chars[i]>='٠' && chars[i]<='٩') chars[i]=(char)('0'+(chars[i]-'٠'));
                else if(chars[i]=='٫') chars[i]='.';
                else if(chars[i]=='٬') chars[i]=',';
            }
            return new string(chars);
        }

        private void CreateBankCatalogEntry(object sender, RoutedEventArgs e)
        {
            try
            {
                SystemCatalogService.AddBank(_db,_bankCode.Text,_bankName.Text,_userId);
                _bankCode.Clear(); _bankName.Clear(); RefreshAll();
                MessageBox.Show("نام بانک به فهرست مؤسسات اضافه شد. برای ثبت حساب واقعی باید حساب بانکی جداگانه تعریف شود.",
                    "مازندشت",MessageBoxButton.OK,MessageBoxImage.Information);
            }
            catch(Exception ex) { MessageBox.Show(ex.Message,"خطا در تعریف بانک",MessageBoxButton.OK,MessageBoxImage.Warning); }
        }

        private void CreateOperationCatalogEntry(object sender, RoutedEventArgs e)
        {
            try
            {
                var direction = (_operationDirection.SelectedItem as ComboBoxItem).Tag.ToString();
                SystemCatalogService.AddOperation(_db,_operationCode.Text,_operationName.Text,direction,
                    _operationGroup.SelectedValue as string,_userId);
                _operationCode.Clear(); _operationName.Clear(); RefreshAll();
                MessageBox.Show("نوع عملیات تعریف شد. این تعریف به‌تنهایی سند یا گردش مالی/موجودی ایجاد نمی‌کند.",
                    "مازندشت",MessageBoxButton.OK,MessageBoxImage.Information);
            }
            catch(Exception ex) { MessageBox.Show(ex.Message,"خطا در تعریف عملیات",MessageBoxButton.OK,MessageBoxImage.Warning); }
        }

        private void CreateParty(object sender, RoutedEventArgs e)
        {
            try {
                MasterDataService.CreateQuickParty(_db,_partyName.Text,_partyNationalId.Text,_partyPhone.Text,_partyAddress.Text,_userId);
                _partyName.Clear(); _partyNationalId.Clear(); _partyPhone.Clear(); _partyAddress.Clear(); RefreshAll();
                MessageBox.Show("طرف حساب با کد یکتای خودکار ثبت شد.","مازندشت",MessageBoxButton.OK,MessageBoxImage.Information);
            } catch(Exception ex) { MessageBox.Show(ex.Message,"خطا در ثبت شخص",MessageBoxButton.OK,MessageBoxImage.Warning); }
        }
        private void CreateGroup(object sender, RoutedEventArgs e)
        {
            try { MasterDataService.CreateQuickProductGroup(_db,_groupName.Text,null,_userId); _groupName.Clear(); RefreshAll(); }
            catch(Exception ex) { MessageBox.Show(ex.Message,"خطا",MessageBoxButton.OK,MessageBoxImage.Warning); }
        }
        private void CreateProduct(object sender, RoutedEventArgs e)
        {
            try {
                var group = _productGroup.SelectedValue as string;
                var unit = _productUnit.SelectedValue as string;
                MasterDataService.CreateQuickProduct(_db,_productName.Text,group,unit,_userId);
                _productName.Clear(); RefreshAll();
            } catch(Exception ex) { MessageBox.Show(ex.Message,"خطا",MessageBoxButton.OK,MessageBoxImage.Warning); }
        }
        private void CreateWarehouse(object sender,RoutedEventArgs e)
        {
            try {
                MasterDataService.CreateQuickWarehouse(_db,_warehouseName.Text,_warehouseLocation.Text,_userId);
                _warehouseName.Clear(); _warehouseLocation.Clear(); RefreshAll();
            } catch(Exception ex) { MessageBox.Show(ex.Message,"خطا در ثبت انبار",MessageBoxButton.OK,MessageBoxImage.Warning); }
        }

        private void CreatePackaging(object sender, RoutedEventArgs e)
        {
            try {
                int tare = 0;
                if (!String.IsNullOrWhiteSpace(_tare.Text) && (!Int32.TryParse(NormalizeDigits(_tare.Text),out tare) || tare < 0))
                    throw new ArgumentException("وزن ظرف باید عدد صحیح نامنفی بر حسب گرم باشد.");
                var kind = (_packKind.SelectedItem as ComboBoxItem).Tag.ToString();
                MasterDataService.CreateQuickPackaging(_db,_packName.Text,kind,tare,_userId);
                _packName.Clear(); _tare.Clear(); RefreshAll();
            } catch(Exception ex) { MessageBox.Show(ex.Message,"خطا",MessageBoxButton.OK,MessageBoxImage.Warning); }
        }
    }

    public sealed class WeighingEntryWindow : Window
    {
        private readonly string _db;
        private readonly string _userId;
        private ComboBox _direction, _party, _productGroup, _product, _packaging, _warehouse;
        private TextBox _grossKg, _basketCount, _lossPercent, _ticket, _notes;

        public WeighingEntryWindow(string databasePath, string userId)
        {
            _db=databasePath; _userId=userId;
            Title="ثبت باسکول | مازندشت"; Width=760; Height=700;
            WindowStartupLocation=WindowStartupLocation.CenterOwner; FlowDirection=FlowDirection.RightToLeft;
            Background=new SolidColorBrush(Color.FromRgb(11,23,17)); Foreground=Brushes.White;
            var root=new DockPanel { Margin=new Thickness(24) };
            var title=new TextBlock { Text="ثبت توزین و گردش موجودی", FontSize=24, FontWeight=FontWeights.Bold, Margin=new Thickness(0,0,0,16) };
            DockPanel.SetDock(title,Dock.Top); root.Children.Add(title);
            var body=new StackPanel();
            _direction=Combo("جهت عملیات",body,new[]{new LookupItem{Id="IN",Code="IN",Name="ورود بار"},new LookupItem{Id="OUT",Code="OUT",Name="خروج بار"}});
            _party=Combo("طرف حساب (انتخاب از فهرست یا ورود کد)",body,MasterDataService.Parties(_db),false,AddParty);
            _productGroup=Combo("گروه محصول",body,MasterDataService.ProductGroups(_db),false,AddGroup);
            _product=Combo("نوع محصول (وابسته به گروه انتخاب‌شده)",body,new System.Collections.Generic.List<LookupItem>(),false,AddProduct);
            _productGroup.SelectionChanged += (s,e) => RefreshProducts();
            RefreshProducts();
            _packaging=Combo("نوع بسته‌بندی",body,MasterDataService.Packaging(_db),true,AddPackaging);
            _warehouse=Combo("انبار",body,MasterDataService.Warehouses(_db),false,AddWarehouse);
            _grossKg=Input(body,"وزن ناخالص (کیلوگرم؛ حداکثر ۳ رقم اعشار)");
            _basketCount=Input(body,"تعداد سبد / بسته");
            _lossPercent=Input(body,"درصد افت (در صورت نیاز؛ پیش‌فرض صفر)");
            _ticket=Input(body,"شماره قبض باسکول (اختیاری)");
            _notes=Input(body,"توضیحات (اختیاری)");
            var save=new Button { Content="ثبت سند و ارسال برای بررسی مدیر", Height=46, Margin=new Thickness(4,18,4,4),
                Background=new SolidColorBrush(Color.FromRgb(212,105,9)), Foreground=Brushes.White, FontWeight=FontWeights.Bold };
            save.Click+=Save; body.Children.Add(save);
            var note=new TextBlock { Text="خروج بار فقط از موجودی تأییدشده همان محصول و انبار تخصیص می‌گیرد. سند پس از ثبت قطعی نیست و تا تأیید مدیر در وضعیت بررسی باقی می‌ماند.",
                TextWrapping=TextWrapping.Wrap, Foreground=new SolidColorBrush(Color.FromRgb(175,193,180)), Margin=new Thickness(5,10,5,0) };
            body.Children.Add(note);
            var scroll=new ScrollViewer { VerticalScrollBarVisibility=ScrollBarVisibility.Auto, Content=body };
            root.Children.Add(scroll); Content=root;
        }

        private ComboBox Combo(string label, Panel parent, System.Collections.Generic.IEnumerable<LookupItem> items, bool optional=false, RoutedEventHandler addClick=null)
        {
            var box=new StackPanel { Margin=new Thickness(4,5,4,5) };
            box.Children.Add(new TextBlock { Text=label, Foreground=Brushes.White, Margin=new Thickness(2,0,2,4) });
            var list = new System.Collections.Generic.List<LookupItem>(items);
            if (optional) list.Insert(0,new LookupItem { Id="",Name="بدون بسته‌بندی" });
            var combo=new ComboBox { Height=36, DisplayMemberPath="Display", ItemsSource=list, SelectedValuePath="Id",
                IsEditable=true, IsTextSearchEnabled=true, StaysOpenOnEdit=true,
                Background=new SolidColorBrush(Color.FromRgb(29,48,38)), Foreground=Brushes.Black };
            TextSearch.SetTextPath(combo,"Display");
            if (combo.Items.Count>0) combo.SelectedIndex=0;
            var row=new DockPanel();
            if(addClick!=null)
            {
                var plus=new Button { Content="＋", Width=42, Height=36, Margin=new Thickness(5,0,0,0),
                    Background=new SolidColorBrush(Color.FromRgb(212,105,9)), Foreground=Brushes.White, FontWeight=FontWeights.Bold };
                plus.Click+=addClick; DockPanel.SetDock(plus,Dock.Left); row.Children.Add(plus);
            }
            row.Children.Add(combo); box.Children.Add(row); parent.Children.Add(box); return combo;
        }
        private TextBox Input(Panel parent,string label)
        {
            var box=new StackPanel { Margin=new Thickness(4,5,4,5) };
            box.Children.Add(new TextBlock { Text=label, Foreground=Brushes.White, Margin=new Thickness(2,0,2,4) });
            var input=new TextBox { Height=36, VerticalContentAlignment=VerticalAlignment.Center, Padding=new Thickness(8),
                Background=new SolidColorBrush(Color.FromRgb(29,48,38)), Foreground=Brushes.White, BorderBrush=new SolidColorBrush(Color.FromRgb(90,125,100)) };
            box.Children.Add(input); parent.Children.Add(box); return input;
        }
        private void AddParty(object sender,RoutedEventArgs e)
        {
            var dialog=new QuickAddDialog("تعریف سریع طرف حساب","نام و نام خانوادگی / نام مجموعه","کد ملی (اختیاری)","شماره تماس","آدرس") { Owner=this };
            if(dialog.ShowDialog()!=true) return;
            try {
                MasterDataService.CreateQuickParty(_db,dialog.Values[0],dialog.Values[1],dialog.Values[2],dialog.Values[3],_userId);
                _party.ItemsSource=MasterDataService.Parties(_db);
                _party.SelectedValue=MasterDataService.Parties(_db).Find(x=>x.Name==dialog.Values[0])?.Id;
            } catch(Exception ex) { MessageBox.Show(ex.Message,"ثبت طرف حساب",MessageBoxButton.OK,MessageBoxImage.Warning); }
        }
        private void AddGroup(object sender,RoutedEventArgs e)
        {
            var dialog=new QuickAddDialog("تعریف سریع گروه محصول","نام گروه") { Owner=this };
            if(dialog.ShowDialog()!=true) return;
            try {
                MasterDataService.CreateQuickProductGroup(_db,dialog.Values[0],null,_userId);
                _productGroup.ItemsSource=MasterDataService.ProductGroups(_db);
                _productGroup.SelectedValue=MasterDataService.ProductGroups(_db).Find(x=>x.Name==dialog.Values[0])?.Id;
                RefreshProducts();
            } catch(Exception ex) { MessageBox.Show(ex.Message,"ثبت گروه",MessageBoxButton.OK,MessageBoxImage.Warning); }
        }
        private void AddProduct(object sender,RoutedEventArgs e)
        {
            var dialog=new QuickAddDialog("تعریف سریع محصول","نام محصول") { Owner=this };
            if(dialog.ShowDialog()!=true) return;
            try {
                var units=MasterDataService.Units(_db);
                MasterDataService.CreateQuickProduct(_db,dialog.Values[0],_productGroup.SelectedValue as string,units.Count>0?units[0].Id:null,_userId);
                RefreshProducts();
                _product.SelectedValue=MasterDataService.ProductsByGroup(_db,_productGroup.SelectedValue as string).Find(x=>x.Name==dialog.Values[0])?.Id;
            } catch(Exception ex) { MessageBox.Show(ex.Message,"ثبت محصول",MessageBoxButton.OK,MessageBoxImage.Warning); }
        }
        private void AddWarehouse(object sender,RoutedEventArgs e)
        {
            var dialog=new QuickAddDialog("تعریف سریع انبار","نام انبار","موقعیت / توضیحات") { Owner=this };
            if(dialog.ShowDialog()!=true) return;
            try {
                MasterDataService.CreateQuickWarehouse(_db,dialog.Values[0],dialog.Values[1],_userId);
                _warehouse.ItemsSource=MasterDataService.Warehouses(_db);
                _warehouse.SelectedValue=MasterDataService.Warehouses(_db).Find(x=>x.Name==dialog.Values[0])?.Id;
            } catch(Exception ex) { MessageBox.Show(ex.Message,"ثبت انبار",MessageBoxButton.OK,MessageBoxImage.Warning); }
        }

        private void AddPackaging(object sender,RoutedEventArgs e)
        {
            var dialog=new QuickAddDialog("تعریف سریع بسته‌بندی","نام بسته‌بندی","وزن خالی هر بسته (گرم)") { Owner=this };
            if(dialog.ShowDialog()!=true) return;
            try {
                int tare=0;
                if(!String.IsNullOrWhiteSpace(dialog.Values[1]) && (!Int32.TryParse(NormalizeDigits(dialog.Values[1]),out tare) || tare<0))
                    throw new ArgumentException("وزن خالی باید عدد صحیح نامنفی بر حسب گرم باشد.");
                MasterDataService.CreateQuickPackaging(_db,dialog.Values[0],"BASKET",tare,_userId);
                _packaging.ItemsSource=new System.Collections.Generic.List<LookupItem>(MasterDataService.Packaging(_db));
                _packaging.SelectedValue=MasterDataService.Packaging(_db).Find(x=>x.Name==dialog.Values[0])?.Id;
            } catch(Exception ex) { MessageBox.Show(ex.Message,"ثبت بسته‌بندی",MessageBoxButton.OK,MessageBoxImage.Warning); }
        }

        private void RefreshProducts()
        {
            if (_product == null || _productGroup == null) return;
            var groupId = _productGroup.SelectedValue as string;
            _product.ItemsSource = MasterDataService.ProductsByGroup(_db, groupId);
            if (_product.Items.Count > 0) _product.SelectedIndex = 0;
        }

        private static string Resolve(ComboBox combo, bool optional)
        {
            var text = (combo.Text ?? String.Empty).Trim();
            if (optional && (text.Length == 0 || text == "بدون بسته‌بندی")) return null;
            var items = combo.ItemsSource as System.Collections.Generic.IEnumerable<LookupItem>;
            if (items != null)
                foreach (var item in items)
                    if (String.Equals(item.Code,text,StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(item.Name,text,StringComparison.OrdinalIgnoreCase) ||
                        String.Equals(item.Display,text,StringComparison.OrdinalIgnoreCase))
                        return item.Id;
            var selectedItem = combo.SelectedItem as LookupItem;
            if (selectedItem != null && (String.IsNullOrEmpty(text) ||
                String.Equals(selectedItem.Display,text,StringComparison.OrdinalIgnoreCase) ||
                String.Equals(selectedItem.Name,text,StringComparison.OrdinalIgnoreCase) ||
                String.Equals(selectedItem.Code,text,StringComparison.OrdinalIgnoreCase)))
                return selectedItem.Id;
            throw new ArgumentException("مقدار واردشده در فهرست وجود ندارد؛ ابتدا آن را در اطلاعات پایه تعریف کنید.");
        }

        private static string NormalizeDigits(string value)
        {
            if (String.IsNullOrEmpty(value)) return value;
            var chars=value.ToCharArray();
            for(int i=0;i<chars.Length;i++)
            {
                if(chars[i]>='۰' && chars[i]<='۹') chars[i]=(char)('0'+(chars[i]-'۰'));
                else if(chars[i]>='٠' && chars[i]<='٩') chars[i]=(char)('0'+(chars[i]-'٠'));
                else if(chars[i]=='٫') chars[i]='.';
                else if(chars[i]=='٬') chars[i]=',';
            }
            return new string(chars);
        }

        private void Save(object sender,RoutedEventArgs e)
        {
            try {
                decimal kg;
                var grossText=NormalizeDigits(_grossKg.Text);
                if (!Decimal.TryParse(grossText,NumberStyles.Number,CultureInfo.InvariantCulture,out kg) &&
                    !Decimal.TryParse(grossText,NumberStyles.Number,CultureInfo.GetCultureInfo("fa-IR"),out kg))
                    throw new ArgumentException("وزن ناخالص معتبر نیست.");
                if (kg<=0 || Decimal.Round(kg,3)!=kg) throw new ArgumentException("وزن باید مثبت و حداکثر سه رقم اعشار داشته باشد.");
                int baskets=0;
                if (!String.IsNullOrWhiteSpace(_basketCount.Text) && !Int32.TryParse(NormalizeDigits(_basketCount.Text),out baskets)) throw new ArgumentException("تعداد سبد نامعتبر است.");
                decimal loss=0;
                if (!String.IsNullOrWhiteSpace(_lossPercent.Text) && !Decimal.TryParse(NormalizeDigits(_lossPercent.Text),NumberStyles.Number,CultureInfo.InvariantCulture,out loss))
                    throw new ArgumentException("درصد افت نامعتبر است.");
                if (loss<0 || loss>100) throw new ArgumentException("درصد افت باید بین صفر و صد باشد.");
                var draft=new WeighingDraft {
                    Direction=Resolve(_direction,false),
                    PartyAccountId=Resolve(_party,false),
                    ProductId=Resolve(_product,false),
                    PackagingTypeId=Resolve(_packaging,true),
                    WarehouseId=Resolve(_warehouse,false),
                    GrossWeightGrams=checked((long)(kg*1000m)),
                    BasketCount=baskets,
                    LossRateBasisPoints=Decimal.ToInt32(Decimal.Round(loss*100m,0,MidpointRounding.AwayFromZero)),
                    ScaleTicketNumber=_ticket.Text,
                    Notes=_notes.Text
                };
                var id=WeighingService.SaveDraft(_db,draft,_userId);
                MessageBox.Show("سند باسکول با شناسه داخلی "+id+" ثبت شد و در انتظار بررسی مدیر است.","ثبت موفق",MessageBoxButton.OK,MessageBoxImage.Information);
                DialogResult=true;
            } catch(Exception ex) { MessageBox.Show(ex.Message,"خطا در ثبت باسکول",MessageBoxButton.OK,MessageBoxImage.Warning); }
        }
    }
}
