using System; using System.IO; using System.Globalization; using Bekosan.PhysToSpring;
double worst = 0, worstA = 0; int n = 0;
foreach (var line in File.ReadLines("cases.csv")) {
  var v = Array.ConvertAll(line.Split(','), s => double.Parse(s, CultureInfo.InvariantCulture));
  var pb = new PbJointParams { Integration = v[0] > 0 ? PbIntegration.Advanced : PbIntegration.Simplified, Pull = (float)v[1], Spring = (float)v[2], Stiffness = (float)v[3], Gravity = (float)v[4], GravityFalloff = (float)v[5] };
  int segs = 4, j = (int)v[9];
  var pbs = new PbJointParams[segs]; var rest = new double[segs]; var al = new double[segs]; var ph = new double[segs];
  for (int i = 0; i < segs; ++i) { pbs[i] = pb; rest[i] = v[8]; }
  SpringMapper.ChainAlphas(pbs, rest, al, ph);
  worstA = Math.Max(worstA, Math.Max(Math.Abs(al[j] - v[10]), Math.Abs(ph[j] - v[11])));
  var r = SpringMapper.Map(pb, v[6], v[7] > 0, v[10], v[11]);
  double Rel(double a, double b) => Math.Abs(a - b) / Math.Max(1e-6, Math.Abs(b));
  worst = Math.Max(worst, Math.Max(Rel(r.Stiffness, v[12]), Math.Max(Math.Abs(r.DragForce - v[13]), Rel(r.GravityPower, v[14]))));
  n++;
}
Console.WriteLine($"cases {n}  max param diff {worst:E3}  max alpha/phi diff {worstA:E3}");
