/* Copyright (C) 2012 Fairmat SRL (info@fairmat.com, http://www.fairmat.com/)
 * Author(s): Enrico Degiuli (enrico.degiuli@fairmat.com)
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using DVPLDOM;
using DVPLI;
using Fairmat.MarketData;
using Fairmat.Math;
using Fairmat.Statistics;

namespace Dupire
{
    [Mono.Addins.Extension("/Fairmat/Estimator")]
    public class DupireEstimator : IEstimator, IIntegrable
    {
        private PFunction r;
        private PFunction q;

        public DupireEstimator()
        {
        }

        #region IEstimator implementation

        /// <summary>
        /// Gets the types required by the estimator in order to work:
        /// InterestRateMarketData and CallPriceMarketData are the
        /// required types for this estimator.
        /// </summary>
        /// <param name="settings">The parameter is not used.</param>
        /// <param name="multivariateRequest">The parameter is not used.</param>
        /// <returns>
        /// An array containing the types InterestRateMarketData and CallPriceMarketData.
        /// </returns>
        public Type[] GetRequirements(IEstimationSettings settings, bool multivariateRequest)
        {
            return new Type[] { typeof(InterestRateMarketData), typeof(CallPriceMarketData) };
        }

        /// <summary>
        /// Gets the value requested by the interface ProvidesTo,
        /// returning <see cref="Dupire.DupireProcess"/> as the type.
        /// </summary>
        public Type ProvidesTo
        {
            get
            {
                return typeof(Dupire.DupireProcess);
            }
        }

        public EstimationResult Estimate(System.Collections.Generic.List<object> marketData, IEstimationSettings settings)
        {
            InterestRateMarketData Mdataset = (InterestRateMarketData)marketData[0];
            CallPriceMarketData Hdataset = (CallPriceMarketData)marketData[1];
            EquityCalibrationData HCalData = new EquityCalibrationData(Hdataset, Mdataset);
            this.r = new DVPLDOM.PFunction(null);
            this.q = new DVPLDOM.PFunction(null);
            this.r.Expr = (double[,])ArrayHelper.Concat(HCalData.MaturityDY.ToArray(), HCalData.Rate.ToArray());
            this.q.Expr = (double[,])ArrayHelper.Concat(HCalData.MaturityDY.ToArray(), HCalData.DividendYield.ToArray());
            this.r.Parse(null);
            this.q.Parse(null);

            IFunction impVol = FitImplVolModel(Hdataset);

            Document doc = new Document();
            ProjectROV prj = new ProjectROV(doc);
            doc.Part.Add(prj);
            prj.Symbols.Add(impVol);
            doc.WriteToXMLFile("impVol.fair");

            // todo: spostare nei settings
            int nmat = 100;
            int nstrike = 100;
            double lastMat = Hdataset.Maturity[Hdataset.Maturity.Length - 1];
            double lastStr = Hdataset.Strike[Hdataset.Strike.Length - 1];
            //Vector locVolMat = Vector.Linspace(lastMat / ((double) nmat), lastMat, nmat);
            //Vector locVolStr = Vector.Linspace(lastStr / ((double) nstrike), lastStr, nstrike);
            Vector locVolMat = Vector.Linspace(0.0, lastMat, nmat);
            Vector locVolStr = Vector.Linspace(0.0, lastStr, nstrike);
            Matrix locVolMatrix = new Matrix(nmat, nstrike);
            Integrate integrate = new Integrate(this);
            double sigma, dSigmadk, num, y, den, integral;
            Vector x = new Vector(2);
            for (int i = 0; i < nmat; i++)
            {
                integral = integrate.AdaptLobatto(0.0, locVolMat[i]);

                int j = 0;
                x[0] = locVolMat[i];
                x[1] = locVolStr[j];
                sigma = impVol.Evaluate(x);
                dSigmadk = impVol.Partial(x, 1);
                num = Math.Pow(sigma, 2) + 2.0 * sigma * x[0] * impVol.Partial(x, 0);
                den = 1.0;
                locVolMatrix[i, j] = Math.Sqrt(num / den);

                // The rest of the cycle.
                for (j = 1; j < nstrike; j++)
                {
                    x[1] = locVolStr[j];
                    sigma = impVol.Evaluate(x);
                    dSigmadk = impVol.Partial(x, 1);
                    num = Math.Pow(sigma, 2) + 2.0 * sigma * x[0] *
                        (impVol.Partial(x, 0) + (this.r.Evaluate(x[0]) - this.q.Evaluate(x[0])) * x[1] * dSigmadk);
                    y = Math.Log(locVolStr[j] / Hdataset.S0) + integral;
                    den = System.Math.Pow(1.0 - x[1] * y * dSigmadk / sigma, 2) + x[1] * sigma * x[0] *
                        (dSigmadk - 0.25 * x[1] * sigma * x[0] * dSigmadk * dSigmadk + x[1] * impVol.Partial2(x, 1));
                    locVolMatrix[i, j] = Math.Sqrt(Math.Abs(num / den));
                    //if (double.IsNaN(locVolMatrix[i, j]))
                    //{
                    //    Console.WriteLine("num = {0}, den = {1}", num, den);
                    //    Console.WriteLine("locVolStr = {0}, locVolMat = {1}, y = {2}, dSigmadk = {3}, sigma = {4}, impvol.Partial2 = {5}", x[1], x[0], y, dSigmadk, sigma, impVol.Partial2(x,1));
                    //}
                    Console.WriteLine("locVolMatrix[{0},{1}] = {2}, Part2 = {3}, t = {4}, S = {5}, dSigmadk = {6}, den = {7}, num = {8}, dsigma/dt = {9}", i, j, locVolMatrix[i, j], impVol.Partial2(x, 1), locVolMat[i], locVolStr[j], dSigmadk, den, num, impVol.Partial(x, 0));
                }
            }

            //Console.WriteLine("locVolMatrix = " + locVolMatrix.ToString());

            // Create dupire outputs.
            PFunction2D.PFunction2D localVol = new PFunction2D.PFunction2D(locVolMat, locVolStr, locVolMatrix);
            localVol.Parse(null);
            string[] names = new string[] { "S0" };
            Vector param = new Vector(1);
            param[0] = Hdataset.S0;
            EstimationResult result = new EstimationResult(names, param);
            result.Objects = new object[3];
            result.Objects[0] = this.r;
            result.Objects[1] = this.q;
            result.Objects[2] = localVol;
            Console.WriteLine("r = " + HCalData.Rate.ToString());
            Console.WriteLine("q = " + HCalData.DividendYield.ToString());
            return result;
        }

        #endregion

        #region IIntegrable implementation
        double IIntegrable.IntegrandFunc(double x)
        {
            return (this.q.Evaluate(x) - this.r.Evaluate(x));
        }
        #endregion

        /// <summary>
        /// This method allows to fit the implied volatility using different models.
        /// </summary>
        /// <param name="Hdataset"></param>
        /// <returns></returns>
        IFunction FitImplVolModel(CallPriceMarketData Hdataset)
        {
            int model = 0;
            switch (model)
            {
                case 0:
                    PFunction2D.PFunction2D pf = new PFunction2D.PFunction2D(Hdataset.Maturity, Hdataset.Strike, Hdataset.Volatility);
                    pf.Interpolation = DVPLUtils.EInterpolationType.LEAST_SQUARES;
                    pf.Extrapolation = DVPLUtils.ExtrapolationType.USEMODEL;
                    pf.Parse(null);
                    return pf;
                    break;
                case 1:

                    //define a model for fitting the implied volatility
                    Fairmat.Statistics.LinearModel impVol = new Fairmat.Statistics.LinearModel(
                                                            new Fairmat.Statistics.Predictor[]{
                                                            delegate(Vector xx){ return 1; },
                                                            delegate(Vector xx){ return xx[0]; },
                                                            delegate(Vector xx){ return xx[1]; },
                                                            delegate(Vector xx){ return System.Math.Pow(xx[0], 2); },
                                                            delegate(Vector xx){ return System.Math.Pow(xx[1], 2); },
                                                            delegate(Vector xx){ return xx[0] * xx[1]; }, });

                    // Unroll matrix and coordinate vectors in order to make it suitable
                    // for the Quadratic model implementation.
                    int n = Hdataset.Volatility.R * Hdataset.Volatility.C;
                    Matrix xy = new Matrix(n, 2);
                    Vector z = new Vector(n);
                    int count = 0;
                    for (int x = 0; x < Hdataset.Volatility.R; x++)
                    {
                        for (int y = 0; y < Hdataset.Volatility.C; y++)
                        {
                            xy[count, Range.All] = ((Matrix)new Vector() { Hdataset.Strike[x], Hdataset.Volatility[y] }).T;
                            z[count] = Hdataset.Volatility[x, y];
                            count++;
                        }
                    }

                    impVol.Estimate(xy, z);
                    return impVol;
                    break;
            }

            return null;
        }
    }
}
