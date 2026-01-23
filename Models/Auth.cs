using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace autodraw_plugin.Models.Auth;

public class LoginRequest
{
    public string username { get; set; }
    public string password { get; set; }
}

public class LoginResponse
{
    public string access_token { get; set; }
    public string role { get; set; }
    public string username { get; set; }
    public bool verified { get; set; }
}

