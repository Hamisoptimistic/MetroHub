using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MetroHub.Core.Search;
using Xunit;

namespace MetroHub.Tests;

public sealed class CalculatorAndTransformTests
{
    private readonly CalculatorSource _calcSource = new();
    private readonly TextTransformSource _transformSource = new(new QueryRouter());

    [Fact]
    public async Task SearchAsync_BasicArithmetic_EvaluatesAccurately()
    {
        var query = SearchRanker.ParseQuery("24 * 1.15", 1);
        var results = await _calcSource.SearchAsync(query, CancellationToken.None);

        var item = Assert.Single(results);
        Assert.Equal("= 27.6", item.DisplayName);
        Assert.NotNull(item.PrimaryAction);
    }

    [Fact]
    public async Task SearchAsync_SqrtFunction_EvaluatesAccurately()
    {
        var query = SearchRanker.ParseQuery("sqrt(144)", 1);
        var results = await _calcSource.SearchAsync(query, CancellationToken.None);

        var item = Assert.Single(results);
        Assert.Equal("= 12", item.DisplayName);
    }

    [Fact]
    public async Task SearchAsync_PrecedenceAndParentheses_EvaluatesAccurately()
    {
        var query1 = SearchRanker.ParseQuery("2 + 3 * 4", 1);
        var res1 = await _calcSource.SearchAsync(query1, CancellationToken.None);
        Assert.Equal("= 14", Assert.Single(res1).DisplayName);

        var query2 = SearchRanker.ParseQuery("(2 + 3) * 4", 1);
        var res2 = await _calcSource.SearchAsync(query2, CancellationToken.None);
        Assert.Equal("= 20", Assert.Single(res2).DisplayName);
    }

    [Fact]
    public async Task SearchAsync_ModuloAndPower_EvaluatesAccurately()
    {
        var queryMod = SearchRanker.ParseQuery("10 % 3", 1);
        var resMod = await _calcSource.SearchAsync(queryMod, CancellationToken.None);
        Assert.Equal("= 1", Assert.Single(resMod).DisplayName);

        var queryPow = SearchRanker.ParseQuery("2 ^ 3", 1);
        var resPow = await _calcSource.SearchAsync(queryPow, CancellationToken.None);
        Assert.Equal("= 8", Assert.Single(resPow).DisplayName);
    }

    [Fact]
    public async Task SearchAsync_IncompleteMath_ReturnsEmptyWithoutException()
    {
        var query = SearchRanker.ParseQuery("10,000 × (", 1);
        var results = await _calcSource.SearchAsync(query, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_NonMathAlphaQuery_ReturnsEmpty()
    {
        var query7z = SearchRanker.ParseQuery("7zip", 1);
        var res7z = await _calcSource.SearchAsync(query7z, CancellationToken.None);
        Assert.Empty(res7z);

        var queryChrome = SearchRanker.ParseQuery("chrome", 1);
        var resChrome = await _calcSource.SearchAsync(queryChrome, CancellationToken.None);
        Assert.Empty(resChrome);
    }

    [Fact]
    public void IsCandidateMathExpression_NonMath_AllocatesZeroBytes()
    {
        // Warm up and verify zero heap allocation
        CalculatorSource.IsCandidateMathExpression("chrome".AsSpan());

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool result = CalculatorSource.IsCandidateMathExpression("7zip".AsSpan());
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.False(result);
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void MathPrattParser_FuzzTest_5000Strings_NeverThrows()
    {
        var rng = new Random(42);
        string[] fragments = ["1", "24", "1.15", "+", "-", "*", "/", "%", "^", "×", "÷", "(", ")", "sqrt(", "abs(", " ", "a", "zip", ",", "0"];

        for (int i = 0; i < 5000; i++)
        {
            int fragCount = rng.Next(1, 12);
            var sb = new System.Text.StringBuilder();
            for (int f = 0; f < fragCount; f++)
            {
                sb.Append(fragments[rng.Next(fragments.Length)]);
            }

            string expression = sb.ToString();

            // Must never throw under any input combination
            bool success = MathPrattParser.TryEvaluate(expression.AsSpan(), out double val);
            if (success)
            {
                Assert.False(double.IsNaN(val));
                Assert.False(double.IsInfinity(val));
            }
        }
    }

    [Fact]
    public async Task SearchAsync_TextTransform_Case_GeneratesCasingVariants()
    {
        var query = SearchRanker.ParseQuery("case hello world", 1);
        var results = await _transformSource.SearchAsync(query, CancellationToken.None);

        Assert.True(results.Count >= 6);
        Assert.Contains(results, r => r.DisplayName == "camelCase: helloWorld");
        Assert.Contains(results, r => r.DisplayName == "PascalCase: HelloWorld");
        Assert.Contains(results, r => r.DisplayName == "snake_case: hello_world");
        Assert.Contains(results, r => r.DisplayName == "kebab-case: hello-world");
        Assert.Contains(results, r => r.DisplayName == "UPPERCASE: HELLO WORLD");
        Assert.Contains(results, r => r.DisplayName == "lowercase: hello world");
    }

    [Fact]
    public async Task SearchAsync_TextTransform_Slug_GeneratesKebabCase()
    {
        var query = SearchRanker.ParseQuery("slug My Great Blog Post!", 1);
        var results = await _transformSource.SearchAsync(query, CancellationToken.None);

        var item = Assert.Single(results);
        Assert.Equal("slug: my-great-blog-post", item.DisplayName);
    }

    [Fact]
    public async Task SearchAsync_TextTransform_Len_ReturnsMetrics()
    {
        var query = SearchRanker.ParseQuery("len hello world", 1);
        var results = await _transformSource.SearchAsync(query, CancellationToken.None);

        var item = Assert.Single(results);
        Assert.Equal("Length: 11 characters, 2 words", item.DisplayName);
    }
}

