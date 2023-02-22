﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Org.BouncyCastle.Utilities.Zlib;
using QRCoder;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using TokenCast.Models;
using TokenCastWebApp.Managers.Interfaces;
using Image = SixLabors.ImageSharp.Image;
using Point = SixLabors.ImageSharp.Point;
using Rectangle = SixLabors.ImageSharp.Rectangle;

// For more information on enabling MVC for empty projects, visit https://go.microsoft.com/fwlink/?LinkID=397860

namespace TokenCast.Controllers
{
    [Route("device")]
    public class DeviceController : Controller
    {
        // GET: /<controller>/

        private readonly IDatabase Database;
        private readonly IWebSocketConnectionManager _webSocketConnectionManager;
        private readonly IMemoryCache _memoryCache;

        public DeviceController(IDatabase database,
            IWebSocketConnectionManager webSocketConnectionManager, IMemoryCache memoryCache)
        {
            Database = database;
            _webSocketConnectionManager = webSocketConnectionManager;
            _memoryCache = memoryCache;
        }

        public IActionResult Index()
        {
            return View();
        }

        // GET: Device/Content?deviceId=test
        [HttpGet("content")]
        public async Task<IActionResult> DisplayContent([FromQuery] string deviceId, [FromQuery] int? width,
            [FromQuery] int? height, [FromQuery] int? skip, [FromQuery] int? take)
        {
            if (deviceId == null)
            {
                return BadRequest();
            }

            var content = await Database.GetDeviceContent(deviceId);
            
            // we're using an in memory cache for now
            if (!_memoryCache.TryGetValue(deviceId, out byte[] cachedImage))
            {

                using var webClient = new WebClient();

                var imageBytes = webClient.DownloadData(content.currentDisplay.tokenImageUrl);
                using var stream = new MemoryStream(imageBytes);

                var image = await Image.LoadAsync(stream);
                image = image.Frames.CloneFrame(0);

                if (content.currentDisplay.rotateAngle != 0)
                {
                    image.Mutate(x => { x.Rotate(content.currentDisplay.rotateAngle); });
                }

                var intersection = Rectangle.Intersect(new Rectangle(0, 0, image.Width, image.Height),
                    new Rectangle(content.currentDisplay.Cropper.Left, content.currentDisplay.Cropper.Top,
                        content.currentDisplay.Cropper.Width, content.currentDisplay.Cropper.Height));

                image.Mutate(x => { x.Crop(intersection); });

                var backgroundColor = ColorTranslator.FromHtml(content.currentDisplay.backgroundColor);
                var backgroundImage = new Image<Rgba32>(Configuration.Default,
                    content.currentDisplay.Cropper.Width, content.currentDisplay.Cropper.Height,
                    new Rgba32(backgroundColor.R, backgroundColor.G, backgroundColor.B, backgroundColor.A));

                var offsetX = content.currentDisplay.Cropper.Left;
                var offsetY = content.currentDisplay.Cropper.Top;

                int newPositionX;
                int newPositionY;

                if (offsetX <= 0 && offsetY <= 0)
                {
                    newPositionX = Math.Abs(offsetX);
                    newPositionY = Math.Abs(offsetY);
                }
                else if (offsetX >= 0 && offsetY >= 0)
                {
                    newPositionX = 0;
                    newPositionY = 0;
                }
                else if (offsetX <= 0 && offsetY >= 0)
                {
                    newPositionX = Math.Abs(offsetX);
                    newPositionY = 0;
                }
                else
                {
                    newPositionX = 0;
                    newPositionY = Math.Abs(offsetY);
                }

                backgroundImage.Mutate(bg =>
                {
                    bg.DrawImage(image, new Point(newPositionX, newPositionY), 1);
                });

                var resultStream = new MemoryStream();
                await backgroundImage.SaveAsJpegAsync(resultStream);
                cachedImage = resultStream.ToArray();

                _memoryCache.Set(deviceId, cachedImage);
            }

            using var cachedStream = new MemoryStream(cachedImage);
            var resultImage = await Image.LoadAsync(cachedStream);
            
            if (height.HasValue && width.HasValue)
            {
                resultImage.Mutate(img => img.Resize(width.Value, height.Value)); 
            }

            var resizedStream = new MemoryStream();
            await resultImage.SaveAsJpegAsync(resizedStream);
                
            var inputAsString = Convert.ToBase64String(resizedStream.ToArray());
            var total = inputAsString.Length;

            if (skip.HasValue && take.HasValue)
            {
                inputAsString = new string(inputAsString.Skip(skip.Value).Take(take.Value).ToArray());
            }
            
            return Ok(new
            {
                c = inputAsString,
                t = total
            });
        }

        // GET: LastUpdateTime
        // Used to force refresh of client
        public long LastUpdateTime()
        {
            LastUpdateModel lastUpdated = Database.GetLastUpdateTime().Result;
            if (lastUpdated == null)
            {
                return 0;
            }

            return lastUpdated.time.Ticks;
        }

        //public ActionResult GetQRCode(string url, string color = "black")
        //{
        //    Color qrColor = Color.Black;
        //    if (color == "white")
        //    {
        //        qrColor = Color.White;
        //    }
        //    QRCodeGenerator qrGenerator = new QRCodeGenerator();
        //    QRCodeData qrCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        //    QRCode qrCode = new QRCode(qrCodeData);
        //    Bitmap qrCodeImage = qrCode.GetGraphic(20, qrColor, Color.Transparent, drawQuietZones: true);
        //    using (var stream = new MemoryStream())
        //    {
        //        qrCodeImage.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        //        Byte[] imageOut = stream.ToArray();
        //        return File(imageOut, "image/png");
        //    }
        //}

        /// <summary>
        /// Sets white labeler field for the device
        /// </summary>
        [HttpPost]
        public void SetDeviceWhitelabeler(string deviceId, string whitelabel)
        {
            Database.SetDeviceWhiteLabeler(deviceId, whitelabel).Wait();
            _webSocketConnectionManager.SendMessage(deviceId, new TokenCastWebApp.Models.ClientMessageResponse
            {
                Event = TokenCastWebApp.Models.EventType.NFTUpdated,
                Message = "Event raised!",
                Success = true
            });
        }

        /// <summary>
        /// Get the device count by whitelabeler
        /// </summary>
        [HttpGet]
        public Dictionary<string, Dictionary<string, int>> GetDeviceCount()
        {
            List<DeviceModel> devices = Database.GetAllDevices().Result;
            var whiteLabelerActiveVsTotal = new Dictionary<string, Dictionary<string, int>>();
            foreach (var device in devices)
            {
                if (device.whiteLabeler == null)
                {
                    device.whiteLabeler = "null";
                }

                if (!whiteLabelerActiveVsTotal.ContainsKey(device.whiteLabeler))
                {
                    whiteLabelerActiveVsTotal[device.whiteLabeler] = new Dictionary<string, int>();
                    whiteLabelerActiveVsTotal[device.whiteLabeler]["active"] = 0;
                    whiteLabelerActiveVsTotal[device.whiteLabeler]["total"] = 0;
                }

                whiteLabelerActiveVsTotal[device.whiteLabeler]["total"]++;
                whiteLabelerActiveVsTotal[device.whiteLabeler]["active"] += device.currentDisplay == null ? 0 : 1;
            }

            return whiteLabelerActiveVsTotal;
        }
    }
}