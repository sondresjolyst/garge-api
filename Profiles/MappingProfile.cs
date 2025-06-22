using AutoMapper;
using garge_api.Dtos.Admin;
using garge_api.Dtos.Auth;
using garge_api.Dtos.Electricity;
using garge_api.Dtos.Sensor;
using garge_api.Dtos.Switch;
using garge_api.Dtos.User;
using garge_api.Dtos.Webhook;
using garge_api.Models.Admin;
using garge_api.Models.Electricity;
using garge_api.Models.Sensor;
using garge_api.Models.Switch;
using garge_api.Models.Webhook;
using Microsoft.AspNetCore.Identity;

public class MappingProfile : Profile
{
    public MappingProfile()
    {
        // Admin mappings
        CreateMap<IdentityRole, RoleDto>();
        CreateMap<RolePermission, RolePermissionDto>().ReverseMap();
        CreateMap<User, UserDto>();

        // Electricity mappings
        CreateMap<PriceResponse, PriceResponseDto>();
        CreateMap<AreaPrices, AreaPricesDto>();
        CreateMap<PriceEntry, PriceEntryDto>();

        // Sensor mappings
        CreateMap<CreateSensorDataDto, SensorData>();
        CreateMap<CreateSensorDto, Sensor>();
        CreateMap<Sensor, SensorDto>().ReverseMap();
        CreateMap<UpdateSensorDto, Sensor>();
        CreateMap<SensorData, SensorDataDto>().ReverseMap();

        // Switch mappings
        CreateMap<Switch, SwitchDto>().ReverseMap();
        CreateMap<CreateSwitchDto, Switch>();
        CreateMap<UpdateSwitchDto, Switch>();
        CreateMap<SwitchData, SwitchDataDto>().ReverseMap();
        CreateMap<CreateSwitchDataDto, SwitchData>();

        // User mappings
        CreateMap<UserProfile, UserProfileDto>()
            .ForMember(dest => dest.EmailConfirmed, opt => opt.MapFrom(src => src.User.EmailConfirmed));
        CreateMap<RegisterUserDto, User>();

        // Webhook mappings
        CreateMap<WebhookSubscription, WebhookSubscriptionDto>().ReverseMap();

    }
}
