using garge_api.Helpers;
using Xunit;

namespace garge_api.Tests;

public class PhoneNumberTests
{
    [Theory]
    [InlineData("91234567",        "4791234567")]
    [InlineData("4791234567",      "4791234567")]
    [InlineData("+47 91 23 45 67", "4791234567")]
    [InlineData("(91) 23-45-67",   "4791234567")]
    [InlineData("12345678",        "4712345678")]
    public void TryNormalizeNo_AcceptsValidNorwegianFormats(string input, string expected)
    {
        Assert.True(PhoneNumber.TryNormalizeNo(input, out var result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123")]
    [InlineData("4691234567")]
    [InlineData("479123456789")]
    public void TryNormalizeNo_RejectsInvalid(string input)
    {
        Assert.False(PhoneNumber.TryNormalizeNo(input, out _));
    }
}
