using System.ComponentModel.DataAnnotations;

namespace garge_api.Models.Admin
{
    public class RolePermission
    {
        [Key]
        public int Id { get; set; }
        public required string RoleName { get; set; }
        public required string Permission { get; set; }
    }
}
