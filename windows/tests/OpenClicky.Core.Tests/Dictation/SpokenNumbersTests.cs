// Port of macos/OpenClicky/OpenClickyTests/SpokenNumbersTests.swift.

using OpenClicky.Core.Dictation;

namespace OpenClicky.Core.Tests.Dictation;

public class SpokenNumbersTests
{
    [Fact]
    public void CountedNumbersBecomeDigits()
    {
        Assert.Equal("I have 325 rupees left", SpokenNumbers.Digitize("I have three hundred and twenty five rupees left"));
        Assert.Equal("about 25 people came", SpokenNumbers.Digitize("about twenty-five people came"));
        Assert.Equal("2300 steps", SpokenNumbers.Digitize("two thousand three hundred steps"));
        Assert.Equal("we sold 15", SpokenNumbers.Digitize("we sold fifteen"));
        Assert.Equal("100 days", SpokenNumbers.Digitize("one hundred days"));
    }

    [Fact]
    public void OneToNineStayWords()
    {
        Assert.Equal("one of them brought two books", SpokenNumbers.Digitize("one of them brought two books"));
        Assert.Equal("no one came", SpokenNumbers.Digitize("no one came"));
    }

    [Fact]
    public void ALoneHundredKeepsItsWords()
    {
        Assert.Equal("a hundred people", SpokenNumbers.Digitize("a hundred people"));
    }

    [Fact]
    public void NumbersThatCantBeOneStayAsSaid()
    {
        Assert.Equal("two three four", SpokenNumbers.Digitize("two three four"));
        Assert.Equal("20, 30, 40", SpokenNumbers.Digitize("twenty, thirty, forty"));
    }

    [Fact]
    public void PunctuationAndCapitalsSurvive()
    {
        Assert.Equal("20 people came.", SpokenNumbers.Digitize("Twenty people came."));
        Assert.Equal("is it 50?", SpokenNumbers.Digitize("is it fifty?"));
    }

    [Fact]
    public void PercentBecomesTheSign()
    {
        Assert.Equal("50% off", SpokenNumbers.Digitize("fifty percent off"));
    }

    [Fact]
    public void AndOutsideANumberIsLeftAlone()
    {
        Assert.Equal("bread and 20 eggs", SpokenNumbers.Digitize("bread and twenty eggs"));
        Assert.Equal("a hundred and one things", SpokenNumbers.Digitize("a hundred and one things"));
    }
}
