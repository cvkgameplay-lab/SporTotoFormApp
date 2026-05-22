using Microsoft.Data.SqlClient;
using SporTotoFormApp.Object;
using System.Globalization;

namespace SporTotoFormApp.Data
{
    public sealed class PredictionRepository
    {
        public async Task<int> SaveRunAsync(
            IReadOnlyList<Coupon> coupons,
            int totalRequested,
            string? notes = null,
            IReadOnlyDictionary<string, string>? profileNamesByPrediction = null,
            CancellationToken cancellationToken = default)
        {
            await using var connection = Database.CreateConnection();
            await connection.OpenAsync(cancellationToken);

            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var runId = await InsertRunAsync(connection, transaction, totalRequested, coupons.Count, notes, cancellationToken);

                foreach (var coupon in coupons)
                {
                    var prediction = NormalizePrediction(coupon.prediction);
                    if (prediction.Length != 15)
                    {
                        continue;
                    }

                    var profileName = profileNamesByPrediction != null &&
                        profileNamesByPrediction.TryGetValue(prediction, out var foundProfileName)
                            ? foundProfileName
                            : null;

                    await InsertPredictionAsync(connection, transaction, runId, coupon, prediction, profileName, cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                return runId;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        private static async Task<int> InsertRunAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            int totalRequested,
            int totalGenerated,
            string? notes,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                INSERT INTO PredictionRuns (TotalRequested, TotalGenerated, Notes)
                OUTPUT INSERTED.Id
                VALUES (@TotalRequested, @TotalGenerated, @Notes);
                """,
                connection,
                transaction);

            command.Parameters.AddWithValue("@TotalRequested", totalRequested);
            command.Parameters.AddWithValue("@TotalGenerated", totalGenerated);
            command.Parameters.AddWithValue("@Notes", (object?)notes ?? DBNull.Value);

            var id = await command.ExecuteScalarAsync(cancellationToken);
            return Convert.ToInt32(id);
        }

        private static async Task InsertPredictionAsync(
            SqlConnection connection,
            SqlTransaction transaction,
            int runId,
            Coupon coupon,
            string prediction,
            string? profileName,
            CancellationToken cancellationToken)
        {
            await using var command = new SqlCommand(
                """
                INSERT INTO Predictions
                    (RunId, ProfileName, PredictionLine, Utility, P15Probability, P14Probability, P13Probability, I15, I14, I13, I12)
                VALUES
                    (@RunId, @ProfileName, @PredictionLine, @Utility, @P15Probability, @P14Probability, @P13Probability, @I15, @I14, @I13, @I12);
                """,
                connection,
                transaction);

            command.Parameters.AddWithValue("@RunId", runId);
            command.Parameters.AddWithValue("@ProfileName", (object?)profileName ?? DBNull.Value);
            command.Parameters.AddWithValue("@PredictionLine", prediction);
            command.Parameters.AddWithValue("@Utility", coupon.Utility);
            command.Parameters.AddWithValue("@P15Probability", coupon.P15Probability);
            command.Parameters.AddWithValue("@P14Probability", coupon.P14Probability);
            command.Parameters.AddWithValue("@P13Probability", coupon.P13Probability);
            command.Parameters.AddWithValue("@I15", ParseInt(coupon.bonus.i15));
            command.Parameters.AddWithValue("@I14", ParseInt(coupon.bonus.i14));
            command.Parameters.AddWithValue("@I13", ParseInt(coupon.bonus.i13));
            command.Parameters.AddWithValue("@I12", ParseInt(coupon.bonus.i12));

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static int ParseInt(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return 0;
            }

            var cleaned = new string(raw.Where(char.IsDigit).ToArray());
            return int.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
                ? value
                : 0;
        }

        private static string NormalizePrediction(string prediction)
        {
            if (string.IsNullOrWhiteSpace(prediction))
            {
                return string.Empty;
            }

            return new string(prediction
                .Where(c => !char.IsWhiteSpace(c))
                .Select(char.ToUpperInvariant)
                .ToArray());
        }
    }
}
