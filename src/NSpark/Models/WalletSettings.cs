namespace NSpark.Models;

/// <summary>
/// Server-side wallet preferences stored with the Signing Operators.
/// </summary>
/// <param name="OwnerIdentityPublicKeyHex">Identity public key of the wallet owner (hex).</param>
/// <param name="PrivateEnabled">When true, the wallet opts into privacy features the SOs offer.</param>
/// <param name="MasterIdentityPublicKeyHex">Optional master identity key used when this wallet is a sub-wallet.</param>
public sealed record WalletSettings(
    string OwnerIdentityPublicKeyHex,
    bool PrivateEnabled,
    string? MasterIdentityPublicKeyHex = null);
