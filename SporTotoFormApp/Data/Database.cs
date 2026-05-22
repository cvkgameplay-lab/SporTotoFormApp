using Microsoft.Data.SqlClient;

namespace SporTotoFormApp.Data
{
    public static class Database
    {
        private const string ConnectionString =
            "Server=DESKTOP-27OP6L7;Database=SporTotoFormApp;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;";

        public static SqlConnection CreateConnection()
        {
            return new SqlConnection(ConnectionString);
        }
    }
}
