namespace garge_api.Dtos.Admin
{
    public class RolePermissionDto
    {
        public int Id { get; set; }
        public string RoleName { get; set; } = string.Empty;
        public string Permission { get; set; } = string.Empty;
    }
}