package character5_fla
{
   import com.greensock.*;
   import com.greensock.easing.*;
   import com.greensock.plugins.*;
   import com.jpauclair.Base64;
   import flash.accessibility.*;
   import flash.display.*;
   import flash.errors.*;
   import flash.events.*;
   import flash.external.*;
   import flash.filters.*;
   import flash.geom.*;
   import flash.media.*;
   import flash.net.*;
   import flash.net.drm.*;
   import flash.system.*;
   import flash.text.*;
   import flash.text.ime.*;
   import flash.ui.*;
   import flash.utils.*;
   
   public dynamic class MainTimeline extends MovieClip
   {
      
      public var mapLoadInProgress:Boolean = false;
      
      public var ldr_map:Loader;
      
      public var ProfilePic:MovieClip;
      
      public var btnBackgroundBack:SimpleButton;
      
      public var btnBackgroundNext:SimpleButton;
      
      public var WALKSPEED:Number = 8;
      
      internal var mvTimer:Timer;
      
      internal var mvTimerObj:Object;
      
      public var btnClose:SimpleButton;
      
      public var closeUi:MovieClip;
      
      public var f:MovieClip;
      
      public var mc:MovieClip;
      
      public var boxLoader:MovieClip;
      
      public var Sword:MovieClip;
      
      public var Helm:MovieClip;
      
      public var Armor:MovieClip;
      
      public var Cape:MovieClip;
      
      public var Pet:MovieClip;
      
      public var boxLoaderr:MovieClip;
      
      public var btnFlip:SimpleButton;
      
      public var btnMakeImage:SimpleButton;
      
      public var btnMoveDown:SimpleButton;
      
      public var btnMoveLeft:SimpleButton;
      
      public var btnMoveRight:SimpleButton;
      
      public var btnMoveUp:SimpleButton;
      
      public var btnReset:SimpleButton;
      
      public var btnSizeDown:SimpleButton;
      
      public var btnSizeUp:SimpleButton;
      
      public var pMC:AvatarMC;
      
      public var txtArmor:TextField;
      
      public var txtCape:TextField;
      
      public var txtHelm:TextField;
      
      public var txtName:TextField;
      
      public var txtPet:TextField;
      
      public var txtMisc:TextField;
      
      public var txtWeapon:TextField;
      
      public var serverFilePath:*;
      
      public var bitModalEnabled:*;
      
      public var mcc:MovieClip;
      
      public var mccBounds:MovieClip;
      
      public var mccContents:MovieClip;
      
      public var objChar:Object;
      
      public var bgs:Array;
      
      public var strFrame:String;
      
      public var FilePath:String;
      
      public var BGColor:Number;
      
      public var sBG:*;
      
      public var url:URLRequest;
      
      public var myLoader:Loader;
      
      public var player:MovieClip;
      
      public var bg:MovieClip;
      
      public var playerMove:Point;
      
      public var playerScale:Number;
      
      public var n:Number;
      
      public var waterMark:MovieClip;
      
      public var AssetClassWaterMark:Class;
      
      public var imageLoader:MovieClip;
      
      public var customBg:MovieClip;
      
      public var enableBgDrag:Boolean = false;
      
      public var enableWeaponDrag:Boolean = true;
      
      internal var square:MovieClip = new MovieClip();
      
      public function MainTimeline()
      {
         super();
         addFrameScript(0,this.frame1,11,this.frame12);
      }
      
      private function AddExternalCallbacks() : void
      {
         ExternalInterface.addCallback("loadHelm",this.pMC.loadHelm);
         ExternalInterface.addCallback("loadArmor",this.pMC.loadArmor);
         ExternalInterface.addCallback("loadHair",this.pMC.loadHair);
         ExternalInterface.addCallback("loadWeapon",this.pMC.loadWeapon);
         ExternalInterface.addCallback("loadCape",this.pMC.loadCape);
         ExternalInterface.addCallback("loadPet",this.pMC.loadPet);
         ExternalInterface.addCallback("loadEmote",this.pMC.loadEmote);
         ExternalInterface.addCallback("loadResize",this.pMC.loadResize);
         ExternalInterface.addCallback("loadResizePet",this.pMC.loadResizePet);
         ExternalInterface.addCallback("changeBg",this.changeBg);
         ExternalInterface.addCallback("blurr",this.blurr);
         ExternalInterface.addCallback("changeUserName",this.changeUserName);
         ExternalInterface.addCallback("changeWeaponName",this.changeWeaponName);
         ExternalInterface.addCallback("changeArmorName",this.changeArmorName);
         ExternalInterface.addCallback("changeHelmName",this.changeHelmName);
         ExternalInterface.addCallback("changeCapeName",this.changeCapeName);
         ExternalInterface.addCallback("changePetName",this.changePetName);
         ExternalInterface.addCallback("closeUii",this.closeUii);
         ExternalInterface.addCallback("daggerMode",this.daggerMode);
         ExternalInterface.addCallback("Hair",this.hairColor);
         ExternalInterface.addCallback("Skin",this.skinColor);
         ExternalInterface.addCallback("Eye",this.eyeColor);
         ExternalInterface.addCallback("Trim",this.trimColor);
         ExternalInterface.addCallback("Base",this.baseColor);
         ExternalInterface.addCallback("Accessory",this.accessoryColor);
         ExternalInterface.addCallback("setGender",this.setGender);
         ExternalInterface.addCallback("unarmed",this.unarmed);
         ExternalInterface.addCallback("hideHelm",this.hideHelm);
         ExternalInterface.addCallback("hideCape",this.hideCape);
         ExternalInterface.addCallback("hidePet",this.hidePet);
         ExternalInterface.addCallback("logoxAxis",this.logoxAxis);
         ExternalInterface.addCallback("logoyAxis",this.logoyAxis);
         ExternalInterface.addCallback("logoSize",this.logoSize);
         ExternalInterface.addCallback("customBgSize",this.customBgSize);
         ExternalInterface.addCallback("bgDrag",this.bgDrag);
         ExternalInterface.addCallback("customBgBlur",this.customBgBlur);
         ExternalInterface.addCallback("loadWatermark",this.loadWatermark);
         ExternalInterface.addCallback("loadBackground",this.loadBackground);
         ExternalInterface.addCallback("loadMisc",this.pMC.loadMisc);
         ExternalInterface.addCallback("hideMisc",this.pMC.hideMisc);
         ExternalInterface.addCallback("getMiscInfo",this.pMC.getMiscInfo);
         ExternalInterface.addCallback("hideHair",this.pMC.hideHair);
         ExternalInterface.addCallback("hideArmor",this.pMC.hideArmor);
         ExternalInterface.addCallback("changeMiscName",this.changeMiscName);
         ExternalInterface.addCallback("getAvatarState",this.pMC.getAvatarState);
         ExternalInterface.addCallback("isReady",this.isReady);
         ExternalInterface.addCallback("setFacing",this.pMC.setFacing);
         ExternalInterface.addCallback("showUserName",this.showUserName);
         ExternalInterface.addCallback("setBackgroundColor",this.setBackgroundColor);
         ExternalInterface.addCallback("clearBackground",this.clearBackground);
      }

      // Callbacks are registered on frame 12, so answering at all means the
      // rig is up and load* calls will attach.
      public function isReady() : String
      {
         return "1";
      }
      
      public function changeBg(param1:String) : void
      {
      }
      
      public function blurr(param1:String) : void
      {
         var _loc2_:Boolean = param1 == "True";
         TweenMax.to(this.customBg,1,_loc2_ ? {"blurFilter":{
            "blurX":5,
            "blurY":5
         }} : {"blurFilter":{
            "blurX":5,
            "blurY":5,
            "remove":true
         }});
      }
      
      public function changeUserName(param1:String) : void
      {
         this.txtName.text = param1;
         this.placeNameTag(null);
      }

      // Character name tag, drawn over the avatar's head like the game's
      // name plate. It used to be one of closeUii's item labels, parked in
      // the top-left corner on top of the gear list.
      internal var nameAnchor:Point;

      public function showUserName(param1:String) : void
      {
         var _loc2_:TextFormat = null;
         if(this.nameAnchor == null)
         {
            this.nameAnchor = new Point(0,0);
            this.measureNameAnchor();
            _loc2_ = new TextFormat("Arial",15,16777215,true);
            _loc2_.align = TextFormatAlign.CENTER;
            // Device font: the stage field embeds only its original face,
            // so Arial text rendered as nothing.
            this.txtName.embedFonts = false;
            this.txtName.defaultTextFormat = _loc2_;
            this.txtName.setTextFormat(_loc2_);
            this.txtName.autoSize = TextFieldAutoSize.CENTER;
            this.txtName.selectable = false;
            this.txtName.mouseEnabled = false;
            this.txtName.filters = [new GlowFilter(0,1,4,4,4,1)];
            addEventListener(Event.ENTER_FRAME,this.placeNameTag);
         }
         this.txtName.visible = AvatarMC.isTrue(param1);
         this.placeNameTag(null);
      }

      // Just above the head as currently dressed (tall helms push it up).
      // Only re-measured in the resting pose so emotes don't bounce it.
      private function measureNameAnchor() : void
      {
         // Visible parts only: getBounds also counts the hair hidden under
         // a helm, which floated the tag well above the head.
         var _loc1_:Rectangle = new Rectangle();
         var _loc2_:MovieClip = this.pMC.mcChar.head;
         var _loc3_:int = 0;
         var _loc4_:DisplayObject = null;
         while(_loc3_ < _loc2_.numChildren)
         {
            _loc4_ = _loc2_.getChildAt(_loc3_);
            if(_loc4_.visible)
            {
               _loc1_ = _loc1_.isEmpty() ? _loc4_.getBounds(this.pMC.mcChar) : _loc1_.union(_loc4_.getBounds(this.pMC.mcChar));
            }
            _loc3_++;
         }
         if(_loc1_.height > 0)
         {
            this.nameAnchor.y = _loc1_.top - 8;
         }
      }
      
      private function placeNameTag(param1:Event) : void
      {
         if(this.nameAnchor == null || !this.txtName.visible)
         {
            return;
         }
         if(this.pMC.mcChar.currentLabel == "Idle")
         {
            this.measureNameAnchor();
         }
         var _loc2_:Point = this.globalToLocal(this.pMC.mcChar.localToGlobal(this.nameAnchor));
         this.txtName.x = Math.round(_loc2_.x - this.txtName.width / 2);
         this.txtName.y = Math.round(_loc2_.y - this.txtName.height);
         setChildIndex(this.txtName,numChildren - 1);
      }
      
      public function changeWeaponName(param1:String) : void
      {
         this.txtWeapon.text = param1;
      }
      
      public function changeArmorName(param1:String) : void
      {
         this.txtArmor.text = param1;
      }
      
      public function changeHelmName(param1:String) : void
      {
         this.txtHelm.text = param1;
      }
      
      public function changeCapeName(param1:String) : void
      {
         this.txtCape.text = param1;
      }
      
      public function changePetName(param1:String) : void
      {
         this.txtPet.text = param1;
      }
      
      public function closeUii(param1:String) : void
      {
         var _loc2_:Boolean = AvatarMC.isTrue(param1);
         this.Sword.visible = _loc2_;
         this.Armor.visible = _loc2_;
         this.Helm.visible = _loc2_;
         this.Cape.visible = _loc2_;
         this.Pet.visible = _loc2_;
         this.txtWeapon.visible = _loc2_;
         this.txtArmor.visible = _loc2_;
         this.txtHelm.visible = _loc2_;
         this.txtCape.visible = _loc2_;
         this.txtPet.visible = _loc2_;
         // Created lazily by changeMiscName: absent until a ground rune
         // name was ever set (this used to throw and fail the whole call).
         if(this.txtMisc != null)
         {
            this.txtMisc.visible = _loc2_;
         }
         if(this.pMC.pAV.miscIcon != null)
         {
            this.pMC.pAV.miscIcon.visible = _loc2_;
         }
      }
      
      // The off hand also stays hidden while the weapon is (unarmed).
      public function daggerMode(param1:String) : void
      {
         this.pMC.dualShown = AvatarMC.isTrue(param1);
         this.pMC.mcChar.weaponOff.visible = this.pMC.dualShown && this.pMC.weaponShown;
      }
      
      public function hairColor(param1:String) : void
      {
         this.objChar.intColorHair = param1;
         this.pMC.updateColor(this.objChar);
      }
      
      public function skinColor(param1:String) : void
      {
         this.objChar.intColorSkin = param1;
         this.pMC.updateColor(this.objChar);
      }
      
      public function eyeColor(param1:String) : void
      {
         this.objChar.intColorEye = param1;
         this.pMC.updateColor(this.objChar);
      }
      
      public function trimColor(param1:String) : void
      {
         this.objChar.intColorTrim = param1;
         this.pMC.updateColor(this.objChar);
      }
      
      public function baseColor(param1:String) : void
      {
         this.objChar.intColorBase = param1;
         this.pMC.updateColor(this.objChar);
      }
      
      public function accessoryColor(param1:String) : void
      {
         this.objChar.intColorAccessory = param1;
         this.pMC.updateColor(this.objChar);
      }
      
      private function setGender(param1:String) : void
      {
         this.objChar.strGender = param1;
      }
      
      // hide*/unarmed take "True" to hide; any case is accepted now (the
      // panel used to send "true"/"false", which could never un-hide).
      public function unarmed(param1:String) : void
      {
         this.pMC.setWeaponShown(!AvatarMC.isTrue(param1));
      }
      
      // Hiding the helm shows the hair and the hair's back half again
      // (it used to hide the back hair too and leave a bald head).
      public function hideHelm(param1:String) : void
      {
         this.pMC.setHelmVisibility(!AvatarMC.isTrue(param1));
      }
      
      // Only a loaded cape can be shown (un-hiding before any cape loaded
      // used to reveal the rig's template cape).
      public function hideCape(param1:String) : void
      {
         this.pMC.capeShown = !AvatarMC.isTrue(param1);
         this.pMC.mcChar.cape.visible = this.pMC.capeShown && this.pMC.capeLoaded;
      }
      
      public function hidePet(param1:String) : void
      {
         this.pMC.petShown = !AvatarMC.isTrue(param1);
         if(this.pMC.mc != null)
         {
            this.pMC.mc.visible = this.pMC.petShown;
         }
      }
      
      public function logoxAxis(param1:Number) : void
      {
         this.boxLoaderr.x = param1;
      }
      
      public function logoyAxis(param1:Number) : void
      {
         this.boxLoaderr.y = param1;
      }
      
      public function logoSize(param1:String) : void
      {
         this.boxLoaderr.scaleX = param1;
         this.boxLoaderr.scaleY = param1;
      }
      
      public function customBgSize(param1:String) : void
      {
         this.customBg.scaleX = param1;
         this.customBg.scaleY = param1;
      }
      
      public function weaponDrag(param1:String) : void
      {
         this.enableWeaponDrag = param1 == "True";
         if(this.enableWeaponDrag)
         {
            addEventListener(MouseEvent.MOUSE_DOWN,this.dragwep);
         }
         else
         {
            removeEventListener(MouseEvent.MOUSE_DOWN,this.dragwep);
         }
      }
      
      private function dragwep(param1:MouseEvent) : void
      {
         if(this.enableWeaponDrag == true)
         {
            this.pMC.mcChar.weapon.startDrag();
         }
      }
      
      public function bgDrag(param1:String) : void
      {
         this.enableBgDrag = param1 == "True";
         if(this.enableBgDrag)
         {
            this.customBg.addEventListener(MouseEvent.MOUSE_DOWN,this.dragMoviee);
         }
         else
         {
            this.customBg.removeEventListener(MouseEvent.MOUSE_DOWN,this.dragMoviee);
         }
      }
      
      public function customBgBlur(param1:Number) : void
      {
         TweenMax.to(this.customBg,1,{"blurFilter":{
            "blurX":param1,
            "blurY":param1
         }});
      }
      
      public function loadWatermark(param1:String) : void
      {
         var _loc2_:Loader = new Loader();
         var _loc3_:URLRequest = new URLRequest(param1);
         _loc2_.load(_loc3_);
         this.boxLoaderr.addChild(_loc2_);
         this.boxLoaderr.x = 200;
         this.boxLoaderr.y = 300;
      }
      
      public function loadBackground(param1:String) : void
      {
         this.clearBackground();
         if(param1 == null || param1 == "")
         {
            return;
         }
         var _loc2_:ByteArray = Base64.decode(param1);
         var _loc3_:Loader = new Loader();
         _loc3_.loadBytes(_loc2_);
         this.customBg.addChild(_loc3_);
      }
      
      // Remove the scene (and any color fill) behind the avatar.
      public function clearBackground() : void
      {
         var _loc1_:Loader = null;
         while(this.customBg.numChildren != 0)
         {
            _loc1_ = this.customBg.getChildAt(0) as Loader;
            this.customBg.removeChildAt(0);
            if(_loc1_ != null)
            {
               try
               {
                  _loc1_.unloadAndStop();
               }
               catch(e:Error)
               {
               }
            }
         }
         this.customBg.graphics.clear();
         this.customBg.x = 0;
         this.customBg.y = 0;
      }
      
      // Solid color background (0xRRGGBB): the color picker used to only
      // tint the HTML box hidden behind the Flash window, so it never showed
      // and a loaded scene stayed up.
      public function setBackgroundColor(param1:String) : void
      {
         this.clearBackground();
         this.customBg.graphics.beginFill(uint(Number(param1)) & 16777215,1);
         this.customBg.graphics.drawRect(-4000,-4000,9280,8720);
         this.customBg.graphics.endFill();
      }
      
      public function mvTimerHandler(param1:TimerEvent) : void
      {
         var _loc2_:Object = {};
         if(this.mvTimerObj != null)
         {
            this.pushMove(this.mvTimerObj.mc,this.mvTimerObj.tx,this.mvTimerObj.ty,this.mvTimerObj.sp);
            this.mvTimerObj = null;
            this.mvTimer.reset();
            this.mvTimer.start();
         }
      }
      
      public function mvTimerKill() : void
      {
         this.mvTimer.reset();
         this.mvTimerObj = null;
      }
      
      public function pushMove(param1:MovieClip, param2:int, param3:int, param4:int) : *
      {
         var _loc5_:* = {};
         _loc5_.tx = int(param2);
         _loc5_.ty = int(param3);
         _loc5_.sp = int(param4);
         this.pMC.walkTo(param2,param3,param4);
      }
      
      public function toProperCase(param1:String) : String
      {
         if(param1 == null)
         {
            return param1;
         }
         return param1.slice(0,1).toUpperCase() + param1.slice(1,param1.length).toLowerCase();
      }
      
      public function mcSetColor(param1:MovieClip, param2:String, param3:String) : *
      {
         this.pMC.setColor(param1,param2,param3);
      }
      
      public function update(param1:Event) : *
      {
         if(this.playerMove.x != 0 || this.playerMove.y != 0)
         {
            this.player.x += this.playerMove.x;
            this.player.y += this.playerMove.y;
         }
         if(this.playerScale != 0)
         {
            if(this.player.scaleX < 0)
            {
               this.player.scaleX -= this.playerScale;
            }
            else
            {
               this.player.scaleX += this.playerScale;
            }
            this.player.scaleY += this.playerScale;
         }
      }
      
      public function dragPmc(param1:MouseEvent) : *
      {
         this.pMC.mcChar.startDrag();
      }
      
      public function eee(param1:MouseEvent) : *
      {
      }
      
      public function mMovee(param1:MouseEvent) : void
      {
         var _loc2_:Number = parent.mouseX;
         var _loc3_:Number = parent.mouseY;
         this.pMC.walkTo(_loc2_,_loc3_,15);
      }
      
      public function mMove(param1:MouseEvent) : void
      {
         var _loc2_:Number = parent.mouseX;
         var _loc3_:Number = parent.mouseY;
      }
      
      internal function frame1() : *
      {
         this.customBg.addEventListener(MouseEvent.MOUSE_UP,this.dropMoviee);
         this.f.addEventListener(MouseEvent.CLICK,this.eee);
         this.boxLoaderr.addEventListener(MouseEvent.MOUSE_DOWN,this.dragMovie);
         this.boxLoaderr.addEventListener(MouseEvent.MOUSE_UP,this.dropMovie);
         stage.addEventListener(MouseEvent.MOUSE_MOVE,this.mMove);
         stage.addEventListener(MouseEvent.CLICK,this.mMovee);
         this.mvTimer = new Timer(1000,1);
         this.mvTimer.removeEventListener("timer",this.mvTimerHandler);
         this.mvTimer.addEventListener("timer",this.mvTimerHandler);
         this.bitModalEnabled = true;
         this.objChar = new Object();
         ExternalInterface.call("loadBackground");
         this.objChar.intLevel = 55;
         this.objChar.intColorHair = 6041135;
         this.objChar.intColorSkin = 15119251;
         this.objChar.intColorEye = 6041135;
         this.objChar.intColorTrim = 16318719;
         this.objChar.intColorBase = 16318719;
         this.objChar.intColorAccessory = 65535;
         this.objChar.strGender = "F";
         this.objChar.strClassName = "Test";
         this.objChar.strClassFile = "none";
         this.objChar.strClassLink = "none";
         this.objChar.strArmorName = "Test";
         this.objChar.strHairFile = "none";
         this.objChar.strHairName = "Normal";
         this.objChar.strWeaponFile = "none";
         this.objChar.strWeaponLink = "none";
         this.objChar.strWeaponName = "Test";
         this.objChar.strCapeFile = "none";
         this.objChar.strCapeLink = "none";
         this.objChar.strCapeName = "Test";
         this.objChar.strHelmFile = "none";
         this.objChar.strHelmLink = "none";
         this.objChar.strHelmName = "Test";
         this.objChar.strPetFile = "none";
         this.objChar.strPetLink = "none";
         this.objChar.strPetName = "Test";
         this.objChar.strWeaponType = "Axe";
         this.objChar.ia1 = 0;
         this.objChar.strFaction = "Arcangrove";
         this.strFrame = "Pic";
         this.objChar.strName = "Name";
         this.objChar.strClassName = "Name";
         this.objChar.strWeaponName = "Weapon";
         this.objChar.strArmorName = "Name";
         this.objChar.strHelmName = "Name";
         this.objChar.strCapeName = "Name";
         this.objChar.strPetName = "Name";
         this.objChar.strFaction = "Hero";
         gotoAndStop(12);
         if(this.pMC != null)
         {
            this.pMC.visible = true;
         }
         stop();
      }
      
      private function dragMovie(param1:MouseEvent) : void
      {
         this.boxLoaderr.startDrag();
      }
      
      private function dropMovie(param1:MouseEvent) : void
      {
         this.boxLoaderr.stopDrag();
      }
      
      private function dragMoviee(param1:MouseEvent) : void
      {
         if(this.enableBgDrag == true)
         {
            this.customBg.startDrag();
         }
      }
      
      private function dropMoviee(param1:MouseEvent) : void
      {
         this.customBg.stopDrag();
      }
      
      // First error thrown while wiring up the player on frame 12 (or
      // "none"). Registered before anything else so a broken build can
      // still say why it has no callbacks.
      public var bootError:String = "none";
      
      public function getBootError() : String
      {
         return this.bootError;
      }
      
      internal function frame12() : *
      {
         ExternalInterface.addCallback("getBootError",this.getBootError);
         try
         {
            this.frame12Body();
         }
         catch(e:Error)
         {
            this.bootError = e.toString();
         }
      }
      
      internal function frame12Body() : void
      {
         ExternalInterface.call("loaded");
         this.BGColor = 0;
         this.mcc = this["pMC"];
         this.pMC.pAV.objData = this.objChar;
         this.txtName.text = this.toProperCase(this.objChar.strName);
         this.txtWeapon.text = "" + this.objChar.strWeaponName;
         this.txtName.visible = false;
         this.txtWeapon.visible = false;
         this.txtArmor.visible = false;
         this.txtHelm.visible = false;
         this.txtCape.visible = false;
         this.txtPet.visible = false;
         this.Sword.visible = false;
         this.Armor.visible = false;
         this.Helm.visible = false;
         this.Cape.visible = false;
         this.Pet.visible = false;
         this.AddExternalCallbacks();
         ExternalInterface.call("setBackground");
         this.pMC.mcChar.weaponOff.visible = false;
         this.pMC.mcChar.weaponFist.visible = false;
         this.pMC.mcChar.weaponFistOff.visible = false;
         this.pMC.mcChar.shield.visible = false;
         stop();
         ExternalInterface.call("loaded");
      }
      
      public function changeMiscName(param1:String) : void
      {
         if(this.txtMisc == null)
         {
            this.txtMisc = new TextField();
            this.txtMisc.defaultTextFormat = new TextFormat("Times New Roman",12,16777215);
            this.txtMisc.width = this.txtPet.width;
            this.txtMisc.height = this.txtPet.height;
            this.txtMisc.x = this.txtPet.x;
            this.txtMisc.y = this.txtPet.y + this.txtPet.height + 6;
            this.txtMisc.scaleX = this.txtPet.scaleX;
            this.txtMisc.scaleY = this.txtPet.scaleY;
            this.txtMisc.selectable = false;
            this.txtMisc.visible = false;
            this.addChild(this.txtMisc);
            this.pMC.pAV.miscIcon = new MovieClip();
            this.pMC.pAV.miscIcon.graphics.lineStyle(3,14066976,1);
            this.pMC.pAV.miscIcon.graphics.drawCircle(15,15,14);
            this.pMC.pAV.miscIcon.x = this.txtMisc.x - 30;
            this.pMC.pAV.miscIcon.y = this.txtMisc.y + (this.txtMisc.height - 26) / 2;
            this.pMC.pAV.miscIcon.visible = false;
            this.addChild(this.pMC.pAV.miscIcon);
         }
         this.txtMisc.text = param1;
      }
   }
}

