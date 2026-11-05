using PharmaLIMS.TrustedSigning;

internal static partial class Program
{
    private static void RunEquipmentBoundaryCases()
    {
        Run("EQ001 Link canonical sorted registered parents", () =>
        {
            var source = new EquipmentLinkRequest(5,"WATER",new[]{1,2,3},null);
            var restored = EquipmentLinkContract.Parse(EquipmentLinkContract.CanonicalPayload(source));
            Equal(source.ActivityId, restored.ActivityId);
            Equal(source.Module, restored.Module);
            Equal("1,2,3",string.Join(",",restored.ParentIds));
        });
        Run("EQ001 Link rejects unordered parents", () =>
            Throws<ArgumentException>(() => EquipmentLinkContract.CanonicalPayload(
                new EquipmentLinkRequest(5,"WATER",new[]{2,1},null))));
        Run("EQ001 Link rejects repeated sample", () =>
            Throws<ArgumentException>(() => EquipmentLinkContract.CanonicalPayload(
                new EquipmentLinkRequest(5,"PRM",new[]{1,1},null))));
        Run("EQ001 Link rejects test with multiple sample parents", () =>
            Throws<ArgumentException>(() => EquipmentLinkContract.CanonicalPayload(
                new EquipmentLinkRequest(5,"EM",new[]{1,2},17))));
        Run("EQ001 Link rejects unsupported module", () =>
            Throws<ArgumentException>(() => EquipmentLinkContract.CanonicalPayload(
                new EquipmentLinkRequest(5,"OTHER",new[]{1},null))));
        Run("EQ001 Link rejects extra JSON properties", () =>
            Throws<System.Text.Json.JsonException>(() => EquipmentLinkContract.Parse(
                "{\"ActivityId\":5,\"Module\":\"WATER\",\"ParentIds\":[1],\"ResultId\":null,\"Extra\":true}")));
        Run("EQ001 Confirm canonical timestamps and rowversion", () =>
        {
            var start = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.FromHours(3));
            var value = new EquipmentConfirmRequest(5,new byte[8],start,start.AddHours(1));
            var restored = EquipmentConfirmContract.Parse(EquipmentConfirmContract.CanonicalPayload(value));
            Equal(value.ActivityId, restored.ActivityId);
            Equal(value.Started, restored.Started);
            Equal(value.Ended, restored.Ended);
            Equal(8, restored.ExpectedVersion.Length);
        });
        Run("EQ001 Confirm rejects missing version", () =>
            Throws<ArgumentException>(() => EquipmentConfirmContract.CanonicalPayload(
                new EquipmentConfirmRequest(5,Array.Empty<byte>(),DateTimeOffset.UtcNow,DateTimeOffset.UtcNow))));
        Run("EQ001 Confirm rejects inverted use times", () =>
            Throws<ArgumentException>(() => EquipmentConfirmContract.CanonicalPayload(
                new EquipmentConfirmRequest(5,new byte[8],DateTimeOffset.UtcNow,DateTimeOffset.UtcNow.AddHours(-1)))));
        Run("EQ001 Confirm rejects excessive duration", () =>
            Throws<ArgumentException>(() => EquipmentConfirmContract.CanonicalPayload(
                new EquipmentConfirmRequest(5,new byte[8],DateTimeOffset.UtcNow.AddDays(-400),DateTimeOffset.UtcNow))));
        Run("EQ001 Confirm rejects unknown JSON member", () =>
            Throws<System.Text.Json.JsonException>(() => EquipmentConfirmContract.Parse(
                "{\"ActivityId\":5,\"ExpectedVersion\":\"AAAAAAAAAAA=\",\"Started\":\"2026-11-03T10:00:00+03:00\",\"Ended\":\"2026-11-03T11:00:00+03:00\",\"Extra\":true}")));
    }
}
