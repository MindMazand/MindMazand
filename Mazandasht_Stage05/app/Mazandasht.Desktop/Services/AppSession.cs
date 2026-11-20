namespace Mazandasht.Desktop.Services
{
    public sealed class UserSession
    {
        public string UserId { get; set; }
        public string Username { get; set; }
        public string DisplayName { get; set; }
        public string RoleCode { get; set; }
    }

    public static class AppSession
    {
        public static UserSession CurrentUser { get; set; }
    }
}