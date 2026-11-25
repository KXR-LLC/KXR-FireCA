"""Rothermel surface fire spread reference for KXR-FireCA.

The script is the numeric oracle for the Reference Model (C#) and the Spread Solver (GPU).
Equations: Rothermel (1972) with the Albini (1976) changes, as documented in
Andrews (2018), RMRS-GTR-371. Fuel models: the 13 Anderson (1982) models.

The Rothermel core runs in its native units (ft, lb, min, Btu). Results convert to SI at the boundary.

Usage:
    python rothermel_reference.py            # print the Anderson comparison table
    python rothermel_reference.py --write    # write reference_cases.json next to the script
"""
import json
import math
import os
import sys

RHO_P = 32.0      # oven-dry particle density, lb/ft3
S_T = 0.0555      # total mineral content, fraction
S_E = 0.010       # effective mineral content, fraction
HEAT = 8000.0     # heat content, Btu/lb
SAV_10H = 109.0   # 1/ft
SAV_100H = 30.0   # 1/ft
TONS_AC_TO_LB_FT2 = 2000.0 / 43560.0

FT_MIN_TO_M_S = 0.00508
BTU_FT2_MIN_TO_KW_M2 = 0.189276
FT_TO_M = 0.3048
MPH_TO_FT_MIN = 88.0
M_S_TO_FT_MIN = 196.850394

# code: (name, load 1-h, load 10-h, load 100-h, load live [tons/acre],
#        SAV 1-h, SAV live [1/ft], depth [ft], dead moisture of extinction [percent])
FUEL_MODELS = {
    1: ("Short grass", 0.74, 0.00, 0.00, 0.00, 3500.0, 1500.0, 1.0, 12.0),
    2: ("Timber grass and understory", 2.00, 1.00, 0.50, 0.50, 3000.0, 1500.0, 1.0, 15.0),
    3: ("Tall grass", 3.01, 0.00, 0.00, 0.00, 1500.0, 1500.0, 2.5, 25.0),
    4: ("Chaparral", 5.01, 4.01, 2.00, 5.01, 2000.0, 1500.0, 6.0, 20.0),
    5: ("Brush", 1.00, 0.50, 0.00, 2.00, 2000.0, 1500.0, 2.0, 20.0),
    6: ("Dormant brush", 1.50, 2.50, 2.00, 0.00, 1750.0, 1500.0, 2.5, 25.0),
    7: ("Southern rough", 1.13, 1.87, 1.50, 0.37, 1750.0, 1550.0, 2.5, 40.0),
    8: ("Closed timber litter", 1.50, 1.00, 2.50, 0.00, 2000.0, 1500.0, 0.2, 30.0),
    9: ("Hardwood litter", 2.92, 0.41, 0.15, 0.00, 2500.0, 1500.0, 0.2, 25.0),
    10: ("Timber litter and understory", 3.01, 2.00, 5.01, 2.00, 2000.0, 1500.0, 1.0, 25.0),
    11: ("Light slash", 1.50, 4.51, 5.51, 0.00, 1500.0, 1500.0, 1.0, 15.0),
    12: ("Medium slash", 4.01, 14.03, 16.53, 0.00, 1500.0, 1500.0, 2.3, 20.0),
    13: ("Heavy slash", 7.01, 23.04, 28.05, 0.00, 1500.0, 1500.0, 3.0, 25.0),
}

# Anderson (1982) published values: rate of spread [chains/h], flame length [ft]
# at 5 mi/h midflame wind, 8 percent dead moisture, 100 percent live moisture.
ANDERSON_1982 = {1: (78, 4), 2: (35, 6), 3: (104, 12), 4: (75, 19), 5: (18, 4), 6: (32, 6),
                 7: (20, 5), 8: (1.6, 1.0), 9: (7.5, 2.6), 10: (7.9, 4.8), 11: (6.0, 3.5),
                 12: (13.0, 8.0), 13: (13.5, 10.5)}


def fuel_constants(code):
    """Values that depend on the fuel model only. The Fuel Table stores these values."""
    name, w1, w10, w100, wl, sav1, savl, depth, mx = FUEL_MODELS[code]
    dead = [(w1 * TONS_AC_TO_LB_FT2, sav1), (w10 * TONS_AC_TO_LB_FT2, SAV_10H),
            (w100 * TONS_AC_TO_LB_FT2, SAV_100H)]
    live = (wl * TONS_AC_TO_LB_FT2, savl)
    a_dead = [s * w / RHO_P for w, s in dead]
    a_live = live[1] * live[0] / RHO_P
    a_d = sum(a_dead)
    a_t = a_d + a_live
    f_dj = [a / a_d for a in a_dead]
    f_dead = a_d / a_t
    f_live = a_live / a_t
    sigma_dead = sum(f * s for f, (_, s) in zip(f_dj, dead))
    sigma = f_dead * sigma_dead + f_live * live[1]
    wn_dead = sum(f * w * (1.0 - S_T) for f, (w, _) in zip(f_dj, dead))
    wn_live = live[0] * (1.0 - S_T)
    w_total = sum(w for w, _ in dead) + live[0]
    rho_b = w_total / depth
    beta = rho_b / RHO_P
    beta_op = 3.348 * sigma ** -0.8189
    ratio = beta / beta_op
    gamma_max = sigma ** 1.5 / (495.0 + 0.0594 * sigma ** 1.5)
    a_exp = 133.0 * sigma ** -0.7913
    gamma = gamma_max * ratio ** a_exp * math.exp(a_exp * (1.0 - ratio))
    eta_s = min(1.0, 0.174 * S_E ** -0.19)
    xi = math.exp((0.792 + 0.681 * math.sqrt(sigma)) * (beta + 0.1)) / (192.0 + 0.2595 * sigma)
    wind_c = 7.47 * math.exp(-0.133 * sigma ** 0.55)
    wind_b = 0.02526 * sigma ** 0.54
    wind_e = 0.715 * math.exp(-3.59e-4 * sigma)
    eps_dead = [math.exp(-138.0 / s) for _, s in dead]
    eps_live = math.exp(-138.0 / live[1])
    fine_den = sum(w * e for (w, _), e in zip(dead, eps_dead))
    fine_w = [w * e / fine_den for (w, _), e in zip(dead, eps_dead)]
    live_den = live[0] * math.exp(-500.0 / live[1])
    live_ratio_w = fine_den / live_den if live[0] > 0.0 else 0.0
    return {
        "code": code, "name": name, "depthFt": depth, "sigma": sigma,
        "mxDead": mx / 100.0, "hasLive": live[0] > 0.0,
        "deadMoistureW": f_dj,                          # weights for the dead category moisture
        "fineMoistureW": fine_w,                        # weights for the fine dead moisture
        "liveRatioW": live_ratio_w,                     # W in the live extinction equation
        "irDeadCoef": gamma * wn_dead * HEAT * eta_s,   # Btu/ft2/min at dead damping 1
        "irLiveCoef": gamma * wn_live * HEAT * eta_s,   # Btu/ft2/min at live damping 1
        "xi": xi,
        "sinkDeadW": [rho_b * f_dead * f * e for f, e in zip(f_dj, eps_dead)],
        "sinkLiveW": rho_b * f_live * eps_live,
        "windC": wind_c, "windB": wind_b, "windK": ratio ** -wind_e,
        "slopeK": 5.275 * beta ** -0.3,
        "residenceMin": 384.0 / sigma,
    }


def damping(r):
    r = min(1.0, max(0.0, r))
    return 1.0 - 2.59 * r + 5.11 * r * r - 3.52 * r ** 3


def spread(code, m1, m10, m100, m_live, wind_ft_min, wind_to_deg, slope, upslope_deg,
           apply_wind_limit=True):
    """Head fire behavior for one cell.

    Moisture values are fractions. wind_ft_min is the midflame wind speed.
    wind_to_deg is the azimuth the wind blows toward, clockwise from north.
    slope is rise over run. upslope_deg is the azimuth of the upslope direction.
    """
    k = fuel_constants(code)
    md = [m1, m10, m100]
    m_dead = sum(w * m for w, m in zip(k["deadMoistureW"], md))
    eta_dead = damping(m_dead / k["mxDead"])
    eta_live = 0.0
    if k["hasLive"]:
        m_fine = sum(w * m for w, m in zip(k["fineMoistureW"], md))
        mx_live = max(k["mxDead"], 2.9 * k["liveRatioW"] * (1.0 - m_fine / k["mxDead"]) - 0.226)
        eta_live = damping(m_live / mx_live)
    i_r = k["irDeadCoef"] * eta_dead + k["irLiveCoef"] * eta_live
    sink = sum(w * (250.0 + 1116.0 * m) for w, m in zip(k["sinkDeadW"], md))
    sink += k["sinkLiveW"] * (250.0 + 1116.0 * m_live)
    r0 = i_r * k["xi"] / sink
    phi_w = k["windC"] * wind_ft_min ** k["windB"] * k["windK"] if wind_ft_min > 0.0 else 0.0
    phi_s = k["slopeK"] * slope * slope
    wr, sr = math.radians(wind_to_deg), math.radians(upslope_deg)
    vx = phi_w * math.sin(wr) + phi_s * math.sin(sr)   # east component
    vz = phi_w * math.cos(wr) + phi_s * math.cos(sr)   # north component
    phi_e = math.hypot(vx, vz)
    head_deg = math.degrees(math.atan2(vx, vz)) % 360.0 if phi_e > 0.0 else 0.0
    u_eff = (phi_e / (k["windC"] * k["windK"])) ** (1.0 / k["windB"]) if phi_e > 0.0 else 0.0
    if apply_wind_limit and u_eff > 0.9 * i_r:
        u_eff = 0.9 * i_r
        phi_e = k["windC"] * u_eff ** k["windB"] * k["windK"] if u_eff > 0.0 else 0.0
    r_head = r0 * (1.0 + phi_e)
    lb = min(8.0, 1.0 + 0.25 * (u_eff / MPH_TO_FT_MIN))
    ecc = math.sqrt(lb * lb - 1.0) / lb
    t_r = k["residenceMin"]
    byram = i_r * t_r * r_head / 60.0                  # Btu/ft/s
    flame_ft = 0.45 * byram ** 0.46 if byram > 0.0 else 0.0
    return {
        "r0Mps": r0 * FT_MIN_TO_M_S, "rHeadMps": r_head * FT_MIN_TO_M_S,
        "headDeg": head_deg, "effWindMps": u_eff / M_S_TO_FT_MIN,
        "lengthToBreadth": lb, "eccentricity": ecc,
        "reactionKwM2": i_r * BTU_FT2_MIN_TO_KW_M2, "residenceS": t_r * 60.0,
        "flameLengthM": flame_ft * FT_TO_M,
        "_rHeadChainsH": r_head * 60.0 / 66.0, "_flameFt": flame_ft,
    }


def rate_at_angle(r_head, ecc, theta_deg):
    """Spread rate at angle theta from the head direction, measured from the ignition point."""
    return r_head * (1.0 - ecc) / (1.0 - ecc * math.cos(math.radians(theta_deg)))


def build_cases():
    cases = []
    moistures = [(0.04, 0.05, 0.07, 0.70), (0.08, 0.08, 0.08, 1.00), (0.11, 0.12, 0.13, 1.20)]
    winds = [(0.0, 0.0), (2.0, 0.0), (5.0, 90.0), (10.0, 225.0)]       # m/s midflame, toward-azimuth
    slopes = [(0.0, 0.0), (0.3, 0.0), (0.7, 135.0)]                    # rise/run, upslope azimuth
    for code in FUEL_MODELS:
        for (m1, m10, m100, ml) in moistures:
            for (u, wdir) in winds:
                for (s, sdir) in slopes:
                    out = spread(code, m1, m10, m100, ml, u * M_S_TO_FT_MIN, wdir, s, sdir)
                    cases.append({
                        "in": {"fuel": code, "m1": m1, "m10": m10, "m100": m100, "mLive": ml,
                               "midflameWindMps": u, "windToDeg": wdir,
                               "slope": s, "upslopeDeg": sdir},
                        "out": {key: round(val, 6) for key, val in out.items()
                                if not key.startswith("_")},
                    })
    return cases


if __name__ == "__main__":
    if "--write" in sys.argv:
        path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "reference_cases.json")
        cases = build_cases()
        with open(path, "w") as fh:
            json.dump({"units": "SI, moisture as fraction, azimuth in degrees clockwise from north",
                       "cases": cases}, fh, indent=1)
        print("wrote", len(cases), "cases to", path)
    else:
        print("model  ROS ch/h  flame ft   Anderson 1982 (ROS, flame)")
        for code in FUEL_MODELS:
            o = spread(code, 0.08, 0.08, 0.08, 1.0, 5.0 * MPH_TO_FT_MIN, 0.0, 0.0, 0.0)
            print(f"{code:5d}  {o['_rHeadChainsH']:8.1f}  {o['_flameFt']:8.1f}   {ANDERSON_1982[code]}")
