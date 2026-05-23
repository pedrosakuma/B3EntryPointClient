using B3.EntryPoint.Client.Auth;

namespace B3.EntryPoint.Client.Tests.Auth;

public class CredentialsTests
{
    /// <summary>
    /// Schema 8.4.2 §720 caps <c>CredentialsEncoding.length</c> at
    /// <c>uint8 maxValue="128"</c>. Constructing a longer Credentials
    /// would emit an invalid varData length byte on the wire and the
    /// gateway would reject Negotiate/Establish.
    /// </summary>
    [Fact]
    public void Ctor_Throws_WhenPayloadExceedsSchemaMax()
    {
        var tooLong = new byte[Credentials.MaxLengthBytes + 1];
        var ex = Assert.Throws<ArgumentException>(() => new Credentials(tooLong));
        Assert.Contains("128", ex.Message);
    }

    [Fact]
    public void Ctor_Accepts_AtSchemaMaxBoundary()
    {
        var atMax = new byte[Credentials.MaxLengthBytes];
        var creds = new Credentials(atMax);
        Assert.Equal(Credentials.MaxLengthBytes, creds.AsSpan().Length);
    }
}
