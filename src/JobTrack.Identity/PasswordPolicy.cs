namespace JobTrack.Identity;

/// <summary>
///     The password complexity JobTrack enforces on self-service password changes (through
///     <see cref="Microsoft.AspNetCore.Identity.UserManager{TUser}.ChangePasswordAsync(TUser, string, string)" />):
///     a minimum length plus at least one letter and one digit, deliberately without requiring mixed
///     case or a symbol. Wired up in <see cref="ServiceCollectionExtensions.AddJobTrackIdentityCore" />
///     alongside <see cref="RequiresLetterPasswordValidator" />, which covers the "at least one letter,
///     any case" half that <c>IdentityOptions.Password</c> cannot express on its own.
/// </summary>
public static class PasswordPolicy
{
	public const int MinimumLength = 6;
}
