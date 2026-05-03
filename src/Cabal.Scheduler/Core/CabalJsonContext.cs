using System.Text.Json.Serialization;

namespace Cabal.Scheduler.Core;

[JsonSerializable(typeof(DashboardStats))]
internal partial class CabalJsonContext : JsonSerializerContext
{
}
