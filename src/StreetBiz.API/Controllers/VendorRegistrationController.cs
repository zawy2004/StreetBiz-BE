using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using StreetBiz.Application.Common.Security;
using StreetBiz.Application.DTOs.VendorRegistration;
using StreetBiz.Application.Features.VendorRegistration.SubmitEvidence;
using StreetBiz.Application.Features.VendorRegistration.SubmitRegistration;
using StreetBiz.Application.Features.VendorRegistration.TrackRegistrations;
using StreetBiz.Application.Features.VendorRegistration.UpdateRegistration;
using StreetBiz.Application.Features.VendorRegistration.WithdrawRegistration;

namespace StreetBiz.API.Controllers;

[ApiController]
[Authorize]
[Route("api/vendor/registrations")]
public sealed class VendorRegistrationController(ISender sender) : ControllerBase
{
    /// <summary>REG-01: submit a business registration.</summary>
    [HttpPost]
    public async Task<ActionResult<BusinessRegistrationDto>> Submit(
        SubmitRegistrationRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new SubmitRegistrationCommand(
            request.VendorType, request.DisplayName, request.DeclaredAddress,
            request.AddressLatitude, request.AddressLongitude, request.WardUnitId), cancellationToken);
        return Ok(new { message = RegMessages.Submitted, data = result });
    }

    /// <summary>REG-03: list the caller's registrations and their status.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<BusinessRegistrationDto>>> List(CancellationToken cancellationToken)
        => Ok(await sender.Send(new TrackRegistrationsQuery(), cancellationToken));

    /// <summary>REG-02: upload an evidence document for a registration.</summary>
    [HttpPost("{registrationId:long}/evidence")]
    public async Task<ActionResult<RegistrationEvidenceDto>> SubmitEvidence(
        long registrationId, SubmitEvidenceRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new SubmitEvidenceCommand(
            registrationId, request.EvidenceType, request.FileUrl, request.OcrExtractedData), cancellationToken);
        return Ok(result);
    }

    /// <summary>REG-04: update and re-submit an editable registration.</summary>
    [HttpPut("{registrationId:long}")]
    public async Task<ActionResult<BusinessRegistrationDto>> Update(
        long registrationId, UpdateRegistrationRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new UpdateRegistrationCommand(
            registrationId, request.VendorType, request.DisplayName, request.DeclaredAddress,
            request.AddressLatitude, request.AddressLongitude, request.WardUnitId), cancellationToken);
        return Ok(result);
    }

    /// <summary>REG-05: withdraw a registration.</summary>
    [HttpPost("{registrationId:long}/withdraw")]
    public async Task<IActionResult> Withdraw(long registrationId, CancellationToken cancellationToken)
    {
        await sender.Send(new WithdrawRegistrationCommand(registrationId), cancellationToken);
        return Ok(new { message = RegMessages.Withdrawn });
    }
}
