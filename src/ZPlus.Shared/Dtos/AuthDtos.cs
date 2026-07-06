namespace ZPlus.Shared.Dtos;

public record RegisterRequest(string Email, string DisplayName, string Password);

public record LoginRequest(string Email, string Password);

public record AuthResponse(string Token, UserDto User);

public record UserDto(Guid Id, string Email, string DisplayName, string Role);
