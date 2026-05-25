using garge_api.Helpers;
using Xunit;

namespace garge_api.Tests;

public class RegistrationCodeTests
{
    [Theory]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(12)]
    public void Generate_ProducesCodeOfRequestedLengthFromAlphabet(int length)
    {
        var code = RegistrationCode.Generate(length);

        Assert.Equal(length, code.Length);
        Assert.All(code, c => Assert.Contains(c, RegistrationCode.Alphabet));
    }

    [Fact]
    public void Alphabet_ExcludesAmbiguousCharacters()
    {
        Assert.DoesNotContain('I', RegistrationCode.Alphabet);
        Assert.DoesNotContain('O', RegistrationCode.Alphabet);
        Assert.DoesNotContain('0', RegistrationCode.Alphabet);
        Assert.DoesNotContain('1', RegistrationCode.Alphabet);
    }

    [Fact]
    public async Task GenerateUniqueAsync_RetriesUntilPredicateAllows()
    {
        // Reject the first two candidates, accept the third. Verifies the loop keeps trying.
        var calls = 0;
        var code = await RegistrationCode.GenerateUniqueAsync(_ =>
        {
            calls++;
            return Task.FromResult(calls < 3);
        });

        Assert.Equal(3, calls);
        Assert.Equal(10, code.Length);
    }

    [Fact]
    public async Task GenerateUniqueAsync_ReturnsImmediatelyWhenFree()
    {
        var code = await RegistrationCode.GenerateUniqueAsync(_ => Task.FromResult(false));

        Assert.Equal(10, code.Length);
    }
}
