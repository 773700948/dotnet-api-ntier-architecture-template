﻿using AutoMapper;
using Breeze.DbCore.UnitOfWork;
using Breeze.Models.ApplicationEnums;
using Breeze.Models.Constants;
using Breeze.Models.Dtos.Auth.Request;
using Breeze.Models.Dtos.Auth.Response;
using Breeze.Models.Dtos.Email.Request;
using Breeze.Models.Dtos.OTP.Request;
using Breeze.Models.Entities;
using Breeze.Models.ModelMapping;
using Breeze.Services.Cache;
using Breeze.Services.ClaimResolver;
using Breeze.Services.HttpHeader;
using Breeze.Services.OTP;
using Breeze.Services.TokenService;
using Breeze.Utilities;
using Microsoft.AspNetCore.Identity;
using System.Transactions;

namespace Breeze.Services.Auth;
public class AuthService : IAuthService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IMapper _mapper;
    private readonly IHttpHeaderService _httpHeaderService;
    private readonly ICacheService _cacheService;
    private readonly SignInManager<UserEntity> _signInManager;
    private readonly UserManager<UserEntity> _userManager;
    private readonly IOTPService _otpService;
    private readonly ITokenService _tokenService;
    private readonly IClaimResolverService _claimResolverService;

    public AuthService(IUnitOfWork unitOfWork,
        IHttpHeaderService httpHeaderService,
        IMapper mapper,
        ICacheService cacheService,
        SignInManager<UserEntity> signInManager,
        UserManager<UserEntity> userManager,
        IOTPService otpService,
        ITokenService tokenService,
        IClaimResolverService claimResolverService)
    {
        _unitOfWork = unitOfWork;
        _httpHeaderService = httpHeaderService;
        _mapper = mapper;
        _cacheService = cacheService;
        _signInManager = signInManager;
        _userManager = userManager;
        _otpService = otpService;
        _tokenService = tokenService;
        _claimResolverService = claimResolverService;
    }


    public async Task<(ResponseEnums, UserResponseDto?)> Register(RegisterRequestDto requestDto)
    {
        if (await UserExists(requestDto.UserName))
        {
            return (ResponseEnums.UserAlreadyExist, null);
        }

        if (string.IsNullOrWhiteSpace(requestDto.OTPCode))
        {
            await _otpService.InvalidateExistingOTPs(requestDto.UserName);
            var otpResponseDto = _otpService.GenerateOTP(_mapper.Map<GenerateOTPRequestDto>(requestDto));
            await _otpService.SaveOTP(_mapper.Map<SaveOTPRequestDto>(otpResponseDto));
            await _otpService.SendOTPEmail(_mapper.Map<OTPEmailRequestDTO>(otpResponseDto));
            return (ResponseEnums.VerificationCodeSent, null);
        }

        if (!await _otpService.IsValideOTP(_mapper.Map<VerifyOTPRequestDto>(requestDto)))
        {
            return (ResponseEnums.InvalidVerificationCode, null);
        }

        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            var user = requestDto.ToUserEntity(_httpHeaderService.GetHeader(PropertyNames.DEVICE_ID).ToString());

            while (await UserhandleAlreadyExist(user.UserHandle, user.UserName!))
            {
                user.UserHandle = $"{requestDto.FirstName.Replace(" ", string.Empty).ToLower()}{requestDto.LastName.Replace(" ", string.Empty).ToLower()}{Helper.GenerateRandomNumber()}";
            }

            var userResult = await _userManager.CreateAsync(user, requestDto.Password);

            if (!userResult.Succeeded)
            {
                return (ResponseEnums.UnableToCompleteProcess, null);
            }

            var assignedRoles = await _userManager.AddToRoleAsync(user, UserRoles.ADMIN_ROLE);

            if (!assignedRoles.Succeeded)
            {
                await _userManager.DeleteAsync(user);
                return (ResponseEnums.UnableToCompleteProcess, null);
            }

            var roles = await _userManager.GetRolesAsync(user);

            var token = _tokenService.GenerateToken(user.ToCreateTokenRequesDto(roles.ToList()));

            scope.Complete();

            return (ResponseEnums.UserRegisteredSuccessfully, user.ToUserResponseDto(token));
        }
    }

    public async Task<(ResponseEnums, UserResponseDto?)> Login(LoginRequestDto requestDto)
    {
        var user = await _userManager.FindByNameAsync(requestDto.UserName);
        if (user is null || !await UserExists(requestDto.UserName))
        {
            return (ResponseEnums.InvalidUsernamePassword, null);
        }

        var deviceIdIsTrusted = await ValidateTrustedDevice(user.UserName!, _httpHeaderService.GetHeader(PropertyNames.DEVICE_ID).ToString());
        var signInResult = await _signInManager.CheckPasswordSignInAsync(user, requestDto.Password, false);

        if (!signInResult.Succeeded)
        {
            return (ResponseEnums.InvalidUsernamePassword, null);
        }

        if (!deviceIdIsTrusted && string.IsNullOrWhiteSpace(requestDto.OTPCode))
        {
            await _otpService.InvalidateExistingOTPs(requestDto.UserName);
            var otpResponseDto = _otpService.GenerateOTP(_mapper.Map<GenerateOTPRequestDto>(requestDto));
            await _otpService.SaveOTP(_mapper.Map<SaveOTPRequestDto>(otpResponseDto));
            await _otpService.SendOTPEmail(_mapper.Map<OTPEmailRequestDTO>(otpResponseDto));
            return (ResponseEnums.VerificationCodeSent, null);
        }

        if (!deviceIdIsTrusted && !await _otpService.IsValideOTP(_mapper.Map<VerifyOTPRequestDto>(requestDto)))
        {
            return (ResponseEnums.InvalidVerificationCode, null);
        }

        if (!deviceIdIsTrusted)
        {
            await UpdateDevice(user.UserName!);
        }

        var roles = await _userManager.GetRolesAsync(user);

        var token = _tokenService.GenerateToken(user.ToCreateTokenRequesDto(roles.ToList()));

        return (ResponseEnums.InvalidVerificationCode, user.ToUserResponseDto(token));
    }

    public async Task<(ResponseEnums, UserResponseDto?)> ChangePassword(ChangePasswordRequestDto requestDto)
    {
        var user = await _userManager.FindByNameAsync(requestDto.UserName);
        if (user is null || !await UserExists(requestDto.UserName))
        {
            return (ResponseEnums.InvalidUsernamePassword, null);
        }

        var isValidPassword = await _signInManager.CheckPasswordSignInAsync(user, requestDto.CurrentPassword, false);

        if (!isValidPassword.Succeeded)
        {
            return (ResponseEnums.InvalidPassword, null);
        }

        IdentityResult changePassword = await ChangeUserPassword(user, requestDto.NewPassword);

        if (!changePassword.Succeeded)
        {
            return (ResponseEnums.SomethingWentWrong, null);
        }

        var checkPassword = await _signInManager.CheckPasswordSignInAsync(user, requestDto.NewPassword, false);

        if (!checkPassword.Succeeded)
        {
            return (ResponseEnums.InvalidPassword, null);
        }

        var roles = await _userManager.GetRolesAsync(user);

        var token = _tokenService.GenerateToken(user.ToCreateTokenRequesDto(roles.ToList()));

        return (ResponseEnums.PasswordChangedSuccessfully, user.ToUserResponseDto(token));
    }

    public async Task<(ResponseEnums, UserResponseDto?)> ForgotPassword(ForgotPasswordRequestDto requestDto)
    {

        var user = await _userManager.FindByNameAsync(requestDto.UserName);
        if (user is null || !await UserExists(requestDto.UserName))
        {
            return (ResponseEnums.InvalidUsernamePassword, null);
        }

        if (string.IsNullOrWhiteSpace(requestDto.OTPCode))
        {
            await _otpService.InvalidateExistingOTPs(requestDto.UserName);
            var otpResponseDto = _otpService.GenerateOTP(_mapper.Map<GenerateOTPRequestDto>(requestDto));
            await _otpService.SaveOTP(_mapper.Map<SaveOTPRequestDto>(otpResponseDto));
            await _otpService.SendOTPEmail(_mapper.Map<OTPEmailRequestDTO>(otpResponseDto));

            return (ResponseEnums.VerificationCodeSent, null);
        }

        var isValidOTP = await _otpService.IsValideOTP(_mapper.Map<VerifyOTPRequestDto>(requestDto));

        if (!isValidOTP)
        {
            return (ResponseEnums.InvalidVerificationCode, null);
        }

        await ChangeUserPassword(user, requestDto.NewPassword);

        var roles = await _userManager.GetRolesAsync(user);

        var token = _tokenService.GenerateToken(user.ToCreateTokenRequesDto(roles.ToList()));

        return (ResponseEnums.UserLoginSuccessfully, user.ToUserResponseDto(token));
    }

    public async Task<ResponseEnums> VerifyEmail(VerifyEmailRequestDto requestDto)
    {
        var user = await _userManager.FindByNameAsync(requestDto.UserName);
        if (user is null || !await UserExists(requestDto.UserName))
        {
            return ResponseEnums.UserDoesNotExist;
        }

        if ((!user.EmailConfirmed) && string.IsNullOrWhiteSpace(requestDto.OTPCode))
        {
            await _otpService.InvalidateExistingOTPs(requestDto.UserName);
            var otpResponseDto = _otpService.GenerateOTP(_mapper.Map<GenerateOTPRequestDto>(requestDto));
            await _otpService.SaveOTP(_mapper.Map<SaveOTPRequestDto>(otpResponseDto));
            await _otpService.SendOTPEmail(_mapper.Map<OTPEmailRequestDTO>(otpResponseDto));
            return ResponseEnums.VerificationCodeSent;
        }

        if ((!user.EmailConfirmed) && !await _otpService.IsValideOTP(_mapper.Map<VerifyOTPRequestDto>(requestDto)))
        {
            return ResponseEnums.InvalidVerificationCode;
        }

        if (!user.EmailConfirmed)
        {
            user.EmailConfirmed = true;
            await _userManager.UpdateAsync(user);
        }

        return ResponseEnums.EmailVerifiedSuccessfully;
    }

    public async Task<(ResponseEnums, UserResponseDto?)> UpdateProfile(UpdateProfileRequestDto requestDto)
    {
        var user = await _userManager.FindByNameAsync(requestDto.UserName);
        if (user is null || !await UserExists(requestDto.UserName))
        {
            return (ResponseEnums.UserDoesNotExist, null);
        }

        _mapper.Map(requestDto, user);

        while (await UserhandleAlreadyExist(user.UserHandle, user.UserName!))
        {
            user.UserHandle = $"{requestDto.FirstName.Replace(" ", string.Empty).ToLower()}{requestDto.LastName.Replace(" ", string.Empty).ToLower()}{Helper.GenerateRandomNumber()}";
        }

        user.ModifiedBy = _claimResolverService.GetLoggedInUsername()!;
        user.ModifiedDate = Helper.GetCurrentDate();

        await _userManager.UpdateAsync(user);


        var roles = await _userManager.GetRolesAsync(user);

        var token = _tokenService.GenerateToken(user.ToCreateTokenRequesDto(roles.ToList()));

        return (ResponseEnums.ProfileUpdatedSuccessfully, user.ToUserResponseDto(token));
    }

    public async Task UpdateDevice(string userName)
    {
        var repo = _unitOfWork.GetRepository<UserEntity>();
        var entity = await repo.FindByFirstOrDefaultAsync(x => x.UserName!.ToLower() == userName.ToLower()
        && x.Deleted == false);

        var newDeviceId = _httpHeaderService.GetHeader(PropertyNames.DEVICE_ID).ToString();
        if (entity is not null)
        {
            _cacheService.RemoveData($"{CacheKeys.TRUSTED_DEVICE}{userName}{entity.TrustedDeviceId}");
            _cacheService.SetData($"{CacheKeys.TRUSTED_DEVICE}{userName}{newDeviceId}", newDeviceId);

            entity.TrustedDeviceId = newDeviceId;
            entity.ModifiedBy = userName;
            entity.ModifiedDate = Helper.GetCurrentDate();
            repo.Update(entity);
        }

        await _unitOfWork.CommitAsync();
    }

    public async Task<bool> UserExists(string userName) => await _unitOfWork.GetRepository<UserEntity>()
             .AnyAsync(x => x.UserName!.ToLower() == userName.ToLower()
             && x.Deleted == false);

    public async Task<UserEntity?> GetUserByUsername(string username)
    {
        return await _unitOfWork.GetRepository<UserEntity>().FindByFirstOrDefaultAsync(x => x.UserName!.ToLower() == username.ToLower()
        && x.Deleted == false);
    }

    public async Task<bool> ValidateTrustedDevice(string userName, string deviceId)
    {
        var cacheKey = $"{CacheKeys.TRUSTED_DEVICE}{userName}{deviceId}";
        var cacheTrusted = _cacheService.GetData<string>(cacheKey);
        if (string.IsNullOrWhiteSpace(cacheTrusted))
        {
            var user = await GetUserByUsername(userName);
            if (user is null || user.TrustedDeviceId != deviceId)
            {
                return false;
            }

            if (_cacheService.Exists(cacheKey) == false)
            {
                _cacheService.SetData(cacheKey, user.TrustedDeviceId);
            }
            return true;
        }
        return true;
    }

    public async Task<bool> UserhandleAlreadyExist(string userHandle, string userName)
        => await _unitOfWork.GetRepository<UserEntity>().AnyAsync(x => x.UserHandle == userHandle &&
        x.UserName != userName &&
        x.Deleted == false);

    #region Private methods
    private async Task<IdentityResult> ChangeUserPassword(UserEntity user, string newPassword)
    {
        await _signInManager.SignOutAsync();
        var passwordResetToken = await _userManager.GeneratePasswordResetTokenAsync(user);

        return await _userManager.ResetPasswordAsync(user, passwordResetToken, newPassword);
    }
    #endregion
}
