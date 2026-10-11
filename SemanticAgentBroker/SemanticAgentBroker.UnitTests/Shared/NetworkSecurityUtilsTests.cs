using System.Net;
using SemanticAgentBroker.Shared.Utilities;

namespace SemanticAgentBroker.UnitTests.Shared;

[TestClass]
public class NetworkSecurityUtilsTests
{
    [TestMethod]
    [DataRow("127.0.0.1", true)]
    [DataRow("127.1.2.3", true)]
    [DataRow("10.0.0.1", true)]
    [DataRow("10.255.255.255", true)]
    [DataRow("172.16.0.1", true)]
    [DataRow("172.31.255.255", true)]
    [DataRow("172.32.0.1", false)]
    [DataRow("192.168.0.1", true)]
    [DataRow("192.168.1.254", true)]
    [DataRow("169.254.169.254", true)]
    [DataRow("100.64.0.1", true)]
    [DataRow("100.100.50.25", true)]
    [DataRow("100.127.255.255", true)]
    [DataRow("100.63.255.255", false)]
    [DataRow("100.128.0.1", false)]
    [DataRow("198.18.0.1", true)]
    [DataRow("198.19.255.255", true)]
    [DataRow("198.17.255.255", false)]
    [DataRow("198.20.0.1", false)]
    [DataRow("0.0.0.0", true)]
    [DataRow("::1", true)]
    [DataRow("::", true)]
    [DataRow("fe80::1", true)]
    [DataRow("fc00::1", true)]
    [DataRow("8.8.8.8", false)]
    [DataRow("1.1.1.1", false)]
    [DataRow("93.184.216.34", false)]
    public void IsPrivateOrLoopbackIp_ValidatesCorrectly(string ipString, bool expectedPrivate)
    {
        var ip = IPAddress.Parse(ipString);
        var actual = NetworkSecurityUtils.IsPrivateOrLoopbackIp(ip);
        Assert.AreEqual(expectedPrivate, actual, $"Failed for IP: {ipString}");
    }

    [TestMethod]
    public void IsPrivateOrLoopbackIp_IPv6UnspecifiedAddress_ReturnsTrue()
    {
        Assert.IsTrue(NetworkSecurityUtils.IsPrivateOrLoopbackIp(IPAddress.IPv6Any));
    }

    [TestMethod]
    public void IsPrivateOrLoopbackIp_Null_ReturnsFalse()
    {
        Assert.IsFalse(NetworkSecurityUtils.IsPrivateOrLoopbackIp(null!));
    }
}
