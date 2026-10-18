using SjolystInnovation.AppKit.Auth;

namespace garge_api.Helpers
{
    /// <summary>
    /// The 409 body when the shared deleter refuses an account deletion, in AppKit.Auth's error shape:
    /// the English message and a stable code a client can show its own text for.
    /// </summary>
    public static class AccountDeletionConflict
    {
        /// <summary>
        /// <c>{ message, code }</c> with <see cref="AuthErrorCodes.LastAdmin"/> for <see cref="LastAdminException"/>
        /// and <see cref="AuthErrorCodes.AccountChangeConflict"/> for <see cref="AccountChangeConflictException"/>.
        /// </summary>
        public static object Body(Exception e) => new
        {
            message = e.Message,
            code = e is LastAdminException ? AuthErrorCodes.LastAdmin : AuthErrorCodes.AccountChangeConflict,
        };
    }
}
