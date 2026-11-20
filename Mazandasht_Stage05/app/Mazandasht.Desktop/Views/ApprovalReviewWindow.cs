using System;
using System.Data;
using System.Data.SQLite;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mazandasht.Desktop.Services;

namespace Mazandasht.Desktop.Views
{
    public sealed class ApprovalReviewWindow : Window
    {
        private readonly string _db;
        private readonly string _userId;
        private readonly DataGrid _grid;
        public ApprovalReviewWindow(string databasePath, string userId)
        {
            _db=databasePath; _userId=userId;
            Title="بررسی و تأیید اسناد | مازندشت"; Width=1050; Height=650;
            WindowStartupLocation=WindowStartupLocation.CenterOwner; FlowDirection=FlowDirection.RightToLeft;
            Background=new SolidColorBrush(Color.FromRgb(11,23,17)); Foreground=Brushes.White;
            var root=new DockPanel { Margin=new Thickness(22) };
            var header=new TextBlock { Text="اسناد در انتظار بررسی مدیر", FontSize=23, FontWeight=FontWeights.Bold, Margin=new Thickness(0,0,0,16) };
            DockPanel.SetDock(header,Dock.Top); root.Children.Add(header);
            var footer=new StackPanel { Orientation=Orientation.Horizontal, HorizontalAlignment=HorizontalAlignment.Left, Margin=new Thickness(0,14,0,0) };
            var refresh=new Button { Content="بازخوانی", Padding=new Thickness(18,9,18,9), Margin=new Thickness(4), Background=new SolidColorBrush(Color.FromRgb(55,91,65)), Foreground=Brushes.White };
            refresh.Click+=(s,e)=>LoadRows();
            var approve=new Button { Content="تأیید و قطعی‌سازی سند انتخاب‌شده", Padding=new Thickness(18,9,18,9), Margin=new Thickness(4), Background=new SolidColorBrush(Color.FromRgb(212,105,9)), Foreground=Brushes.White, FontWeight=FontWeights.Bold };
            approve.Click+=Approve;
            footer.Children.Add(refresh); footer.Children.Add(approve);
            DockPanel.SetDock(footer,Dock.Bottom); root.Children.Add(footer);
            _grid=new DataGrid { IsReadOnly=true, AutoGenerateColumns=true, CanUserAddRows=false,
                Background=new SolidColorBrush(Color.FromRgb(23,39,30)), Foreground=Brushes.White, SelectionMode=DataGridSelectionMode.Single };
            root.Children.Add(_grid); Content=root; LoadRows();
        }
        private void LoadRows()
        {
            var table=new DataTable();
            using(var c=new SQLiteConnection("Data Source="+_db+";Version=3;Pooling=True;"))
            {
                c.Open();
                using(var da=new SQLiteDataAdapter(@"SELECT d.id AS شناسه, d.document_number AS شماره_سند, d.document_type AS نوع_سند,
                    d.date_iso AS تاریخ, COALESCE(p.display_name,'—') AS طرف_حساب, COALESCE(w.direction,'—') AS جهت,
                    COALESCE(w.net_weight_g,0)/1000.0 AS وزن_خالص_کیلوگرم, d.created_at_utc AS زمان_ثبت
                    FROM documents d LEFT JOIN party_accounts pa ON pa.id=d.party_account_id
                    LEFT JOIN parties p ON p.id=pa.party_id LEFT JOIN weigh_tickets w ON w.document_id=d.id
                    WHERE d.status='REVIEW' ORDER BY d.created_at_utc;",c))
                    da.Fill(table);
            }
            _grid.ItemsSource=table.DefaultView;
        }
        private void Approve(object sender,RoutedEventArgs e)
        {
            var view=_grid.SelectedItem as DataRowView;
            if(view==null) { MessageBox.Show("ابتدا یک سند را انتخاب کنید."); return; }
            var id=Convert.ToString(view["شناسه"]);
            if(MessageBox.Show("آیا سند انتخاب‌شده را بررسی و قطعی می‌کنید؟ پس از تأیید، تغییر مستقیم سند مجاز نیست.",
                "تأیید مدیر",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes) return;
            try {
                ApprovalService.ApproveDocument(_db,id,_userId,"تأیید از پنجره بررسی مدیر");
                LoadRows();
                MessageBox.Show("سند تأیید شد. گردش موجودی مرتبط نیز قطعی شد.","موفق",MessageBoxButton.OK,MessageBoxImage.Information);
            } catch(Exception ex) { MessageBox.Show(ex.Message,"عدم امکان تأیید",MessageBoxButton.OK,MessageBoxImage.Error); }
        }
    }
}
