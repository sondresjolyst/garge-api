using AutoMapper;
using garge_api.Dtos.Admin;
using garge_api.Dtos.Auth;
using garge_api.Dtos.Electricity;
using garge_api.Dtos.Sensor;
using garge_api.Dtos.Shop;
using garge_api.Dtos.Subscription;
using garge_api.Dtos.Switch;
using garge_api.Dtos.User;
using garge_api.Dtos.Webhook;
using garge_api.Models.Admin;
using garge_api.Models.Electricity;
using garge_api.Models.Sensor;
using garge_api.Models.Shop;
using garge_api.Models.Subscription;
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
        CreateMap<AppSettings, AppSettingsDto>();
        CreateMap<AppSettings, PublicSettingsDto>();
        CreateMap<UpdateAppSettingsDto, AppSettings>();

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
        CreateMap<BatteryHealth, BatteryHealthDto>().ReverseMap();
        CreateMap<CreateBatteryHealthDto, BatteryHealth>();
        CreateMap<SensorActivity, SensorActivityDto>().ReverseMap();
        CreateMap<CreateSensorActivityDto, SensorActivity>();
        CreateMap<UpdateSensorActivityDto, SensorActivity>();

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
        CreateMap<CreateWebhookSubscriptionDto, WebhookSubscription>();

        // Subscription plan mappings
        CreateMap<Product, ProductResponseDto>()
            .ForMember(d => d.Interval, o => o.MapFrom(s => s.Interval.ToString()));
        CreateMap<CreateProductDto, Product>();
        CreateMap<UpdateProductDto, Product>();

        CreateMap<garge_api.Models.Subscription.Subscription, SubscriptionResponseDto>()
            .ForMember(d => d.Status, o => o.MapFrom(s => s.Status.ToString()))
            .ForMember(d => d.Interval, o => o.MapFrom(s => s.Product != null ? s.Product.Interval.ToString() : string.Empty))
            .ForMember(d => d.ProductName, o => o.MapFrom(s => s.Product != null ? s.Product.Name : string.Empty))
            .ForMember(d => d.PriceInOre, o => o.MapFrom(s => s.Product != null ? s.Product.PriceInOre : 0));

        // Shop mappings
        CreateMap<ShopItem, ShopItemResponseDto>();
        CreateMap<CreateShopItemDto, ShopItem>();
        CreateMap<UpdateShopItemDto, ShopItem>();

        CreateMap<Order, OrderResponseDto>()
            .ForMember(d => d.Status, o => o.MapFrom(s => s.Status.ToString()))
            .ForMember(d => d.Items, o => o.MapFrom(s => s.OrderItems))
            .ForMember(d => d.HasInvoice, o => o.MapFrom(s => s.Invoice != null));

        CreateMap<OrderItem, OrderItemResponseDto>()
            .ForMember(d => d.ShopItemName, o => o.MapFrom(s => s.ShopItem != null ? s.ShopItem.Name : string.Empty));

        CreateMap<Invoice, InvoiceResponseDto>();

        CreateMap<Order, AdminOrderResponseDto>()
            .ForMember(d => d.Status, o => o.MapFrom(s => s.Status.ToString()))
            .ForMember(d => d.Items, o => o.MapFrom(s => s.OrderItems))
            .ForMember(d => d.HasInvoice, o => o.MapFrom(s => s.Invoice != null))
            .ForMember(d => d.UserEmail, o => o.MapFrom(s => s.User != null ? s.User.Email ?? string.Empty : string.Empty))
            .ForMember(d => d.UserName, o => o.MapFrom(s => s.User != null ? $"{s.User.FirstName} {s.User.LastName}" : string.Empty));
    }
}
