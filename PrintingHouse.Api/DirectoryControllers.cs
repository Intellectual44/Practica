using PrintingHouse.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace PrintingHouse.Api;

[ApiController]
[Authorize(Policy = "Staff")]
[Route("api/customers")]
[Tags("Заказчики")]
public sealed class CustomersController(ICustomerRepository customers) : ControllerBase
{
    [HttpGet]
    public Task<List<CustomerInfo>> List(CancellationToken ct) => customers.GetCustomers(ct);

    [HttpPost]
    public async Task<IActionResult> Create(CustomerInput input, CancellationToken ct)
    {
        int id = await customers.CreateCustomer(input, ct);
        return Created("/api/customers", new { id });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, CustomerInput input, CancellationToken ct) =>
        await customers.UpdateCustomer(id, input, ct) ? NoContent() : NotFound();
}

[ApiController]
[Route("api/users")]
[Tags("Пользователи")]
public sealed class UsersController(IUserRepository users) : ControllerBase
{
    [HttpGet]
    [Authorize(Roles = Roles.Admin)]
    public Task<List<UserInfo>> List(CancellationToken ct) => users.GetUsers(false, ct);

    [HttpPost]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Create(UserInput input, CancellationToken ct)
    {
        int id = await users.CreateUser(input, ct);
        return Created("/api/users", new { id });
    }

    [HttpPut("{id:int}/access")]
    [Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> SetAccess(int id, UserAccessInput input, CancellationToken ct) =>
        await users.SetUserAccess(id, SessionTokens.User(User).Id, input.IsDisabled, ct) ? NoContent() : NotFound();

    [HttpGet("printers")]
    [Authorize(Policy = "Staff")]
    public Task<List<UserInfo>> Printers(CancellationToken ct) => users.GetUsers(true, ct);
}
