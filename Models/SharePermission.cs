namespace garge_api.Models
{
    /// <summary>
    /// Access level for a shared device row (where <c>IsOwner = false</c>). The owner always has full
    /// rights implicitly; this only describes what a recipient of a share may do.
    /// </summary>
    public enum SharePermission
    {
        /// <summary>View data, history, battery health and live updates. No control or editing.</summary>
        Read = 0,

        /// <summary>Read, plus toggle switches, create/edit automations, and battery calibration.</summary>
        Edit = 1,
    }
}
