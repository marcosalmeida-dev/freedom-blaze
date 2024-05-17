using System.Text.RegularExpressions;
using FreedomBlaze.Enums;

namespace FreedomBlaze.Helpers;

public class BtcAddressHelper
{
    public static BtcAddressType ValidateBtcAddress(string address)
    {
        // Regular expression to match Bech32 Bitcoin address
        string bech32Regex = @"^(bc1|[13])[a-zA-HJ-NP-Z0-9]{25,39}$";

        // Regular expression to match P2PKH Bitcoin address
        string p2pkhRegex = @"^1[a-zA-HJ-NP-Z1-9]{25,34}$";

        // Regular expression to match P2SH Bitcoin address
        string p2shRegex = @"^3[a-zA-HJ-NP-Z1-9]{25,34}$";

        // Regular expression to match Bitcoin Lightning Network address
        string lightningRegex = @"^(ln)([a-zA-Z0-9]+[0-9]+[a-z0-9]+)+$";

        if (Regex.IsMatch(address, lightningRegex))
            return BtcAddressType.BitcoinLightning;
        else if (Regex.IsMatch(address, bech32Regex))
            return BtcAddressType.BitcoinOnChain;
        else if (Regex.IsMatch(address, p2pkhRegex) || Regex.IsMatch(address, p2shRegex))
            return BtcAddressType.BitcoinOnChain;
        else
            return BtcAddressType.Unknown;
    }
}
