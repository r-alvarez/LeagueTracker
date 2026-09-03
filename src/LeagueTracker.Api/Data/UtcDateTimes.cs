using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LeagueTracker.Api.Data;

// Every timestamp column in the app is UTC by name (*Utc). Npgsql refuses a
// DateTime whose Kind is Local or Unspecified for timestamptz, and hands values
// back as Kind Utc; the converter makes both directions unconditional so a
// query parameter built from a parsed string cannot throw and a value read
// back serialises with its Z (a browser shows an expiry an hour off otherwise).
public static class UtcDateTimes
{
    private static readonly ValueConverter<DateTime, DateTime> Utc = new(v => ToUtc(v), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
    private static readonly ValueConverter<DateTime?, DateTime?> UtcNullable = new(
        v => v.HasValue ? ToUtc(v.Value) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    public static void Apply(ModelBuilder builder)
    {
        foreach (var entity in builder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (property.ClrType == typeof(DateTime)) property.SetValueConverter(Utc);
                else if (property.ClrType == typeof(DateTime?)) property.SetValueConverter(UtcNullable);
            }
        }
    }

    private static DateTime ToUtc(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
