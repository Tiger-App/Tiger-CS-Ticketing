using TigerCS.Domain.Modules.Collections;

namespace TigerCS.Tests.Collections.Domain;

/// <summary>The apartment floor is read from the unit number only when the code is a recognisable apartment; anything else has no floor and is never guessed. (The SQL mirror is dbo.fn_CollectionsUnitFloor, V015.)</summary>
public sealed class CollectionsUnitFloorTests
{
    [Theory]
    [InlineData("TP140-101", 1)]
    [InlineData("TP140-1921", 19)]
    [InlineData("TP140-0507", 5)]          // a leading zero is still floor 5
    [InlineData("140-101", 1)]             // the TP prefix is optional
    [InlineData("tp140-205", 2)]           // and case-insensitive
    [InlineData("TP140-001", 0)]           // ground floor
    [InlineData("TP140-1001", 10)]
    [InlineData("TP130-601-A", 6)]         // a single-letter suffix segment
    [InlineData("TP120-601A", 6)]          // a letter glued to the number
    [InlineData("TP136-C-402", 4)]         // a single-letter block segment
    [InlineData("TP140-9999", 99)]
    [InlineData(" TP140-305 ", 3)]
    public void ARecognisableApartment_HasItsFloor(string unitCode, int floor) => Assert.Equal(floor, CollectionsUnitFloor.Parse(unitCode));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TP140")]                  // a tower only
    [InlineData("TP140-")]                 // no unit part
    [InlineData("TP140-12")]               // two digits: not an apartment number
    [InlineData("TP140-12345")]            // five digits
    [InlineData("TP140-P12")]              // parking
    [InlineData("TP140-SHOP1")]            // retail
    [InlineData("TP140-G01")]
    [InlineData("TP140-101-X7")]           // a suffix that is not a single letter
    [InlineData("TP140-101-202")]          // two apartment numbers: ambiguous
    [InlineData("TP140-A-B-C-102")]        // too many segments
    [InlineData("TP140-513*")]             // cancelled
    [InlineData("TP140-1A")]
    public void AnythingElse_HasNoFloor_NeverAGuess(string? unitCode) => Assert.Null(CollectionsUnitFloor.Parse(unitCode));

    [Fact]
    public void NoSelection_IsAllFloors_ASelectionKeepsOnlyApartmentsOnThoseFloors()
    {
        Assert.True(CollectionsUnitFloor.Matches("TP140-P12", null));                     // All floors = no filtering, even for a non-apartment
        Assert.True(CollectionsUnitFloor.Matches("TP140-P12", new HashSet<int>()));
        var floors = new HashSet<int> { 1, 19 };
        Assert.True(CollectionsUnitFloor.Matches("TP140-101", floors));
        Assert.True(CollectionsUnitFloor.Matches("TP140-1921", floors));
        Assert.False(CollectionsUnitFloor.Matches("TP140-201", floors));
        Assert.False(CollectionsUnitFloor.Matches("TP140-P12", floors));                 // not an apartment: never matches a chosen floor
    }

    [Fact]
    public void TheSelection_IsNormalised_AndAMalformedOneIsDetected()
    {
        Assert.Equal([0, 2, 5], CollectionsUnitFloor.Normalize([5, 2, 2, 0]));
        Assert.Empty(CollectionsUnitFloor.Normalize(null));
        Assert.Equal([3], CollectionsUnitFloor.Normalize([3, -1, 100]));
        Assert.True(CollectionsUnitFloor.HasInvalid([3, -1])); Assert.True(CollectionsUnitFloor.HasInvalid([100])); Assert.False(CollectionsUnitFloor.HasInvalid([0, 99])); Assert.False(CollectionsUnitFloor.HasInvalid(null));
        Assert.Equal("Ground floor", CollectionsUnitFloor.Label(0)); Assert.Equal("Floor 12", CollectionsUnitFloor.Label(12));
    }
}
