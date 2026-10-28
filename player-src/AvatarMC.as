package
{
   import com.jpauclair.Base64;
   import flash.display.*;
   import flash.events.*;
   import flash.external.*;
   import flash.filters.*;
   import flash.geom.*;
   import flash.net.*;
   import flash.system.*;
   import flash.utils.*;
   
   [Embed(source="/_assets/assets.swf", symbol="symbol303")]
   public class AvatarMC extends MovieClip
   {
      
      private const MAX_RATIO:Number = 4.6566128752458e-10;
      
      public var mcChar:mcSkel;
      
      public var shadow:MovieClip;
      
      public var strGender:String;
      
      public var noGlow:Boolean = false;
      
      public var pet:Boolean = false;
      
      internal var ldr:Loader;
      
      internal var defaultCT:ColorTransform;
      
      internal var serverFilePath:String = "";
      
      public var pAV:Object;
      
      internal var strSkinLinkage:String;
      
      private var animQueue:Array;
      
      private var animEvents:Object;
      
      private var r:int;
      
      private var randNum:Number;
      
      private var xDep:*;
      
      private var yDep:*;
      
      private var xTar:*;
      
      private var yTar:Number;
      
      private var nDuration:*;
      
      private var nXStep:*;
      
      private var nYStep:*;
      
      private var walkSpeed:Number;
      
      private var op:Point;
      
      private var tp:Point;
      
      private var walkTS:Number;
      
      private var walkD:Number;
      
      private var headPoint:Point;
      
      public var STAGE:MovieClip;
      
      public var ty:Number;
      
      public var px:*;
      
      public var py:*;
      
      public var tx:*;
      
      public var peet:String = "e";
      
      private var topIndex:int = 0;
      
      public var AssetClassPet:Class;
      
      public var mc:MovieClip;
      
      public var petLoaded:Boolean = false;
      
      public var facingRight:Boolean = false;
      
      public var facingLeft:Boolean = false;
      
      private var rr:int;
      
      private var randNumm:Number;
      
      private var xDepp:*;
      
      private var yDepp:*;
      
      private var xTarr:*;
      
      private var yTarr:Number;
      
      private var nDurationn:*;
      
      private var nXStepp:*;
      
      private var nYStepp:*;
      
      private var walkSpeedd:Number;
      
      private var opp:Point;
      
      private var tpp:Point;
      
      private var walkTSS:Number;
      
      private var walkDD:Number;
      
      private var headPointt:Point;
      
      public var STAGEE:MovieClip;
      
      public var tyy:Number;
      
      public var pxx:*;
      
      public var pyy:*;
      
      public var txx:*;
      
      internal var armorDomain:ApplicationDomain;
      
      internal var hairDomain:ApplicationDomain;
      
      internal var weaponDomain:ApplicationDomain;
      
      internal var capeDomain:ApplicationDomain;
      
      internal var helmDomain:ApplicationDomain;
      
      internal var petDomain:ApplicationDomain;
      
      internal var miscDomain:ApplicationDomain;
      
      public function AvatarMC()
      {
         // Field setup after super(): FFDec's compiler emits these ahead of
         // the base constructor otherwise, and touching transform on an
         // unconstructed DisplayObject throws (the rig never binds).
         super();
         this.ldr = new Loader();
         this.defaultCT = MovieClip(this).transform.colorTransform;
         this.pAV = new Object();
         var _loc1_:RegExp = /http:\/\/[\w.]+/i;
         visible = true;
         this.animQueue = [];
         this.r = Math.random() * int.MAX_VALUE;
         this.animEvents = new Object();
         this.mcChar.buttonMode = true;
         this.mcChar.mouseChildren = true;
         this.shadow.mouseEnabled = this.shadow.mouseChildren = false;
         this.headPoint = new Point(0,this.mcChar.head.y - 1.4 * this.mcChar.head.height);
         this.hideOptionalParts();
      }
      
      private function checkQueue(param1:Event) : Boolean
      {
         var _loc2_:MovieClip = null;
         var _loc3_:String = null;
         var _loc4_:int = 0;
         var _loc5_:* = undefined;
         if(this.animQueue.length > 0)
         {
            _loc2_ = MovieClip(stage.getChildAt(0)).world as MovieClip;
            _loc3_ = this.mcChar.currentLabel;
            _loc4_ = this.mcChar.emoteLoopFrame();
            if(_loc2_.combatAnims.indexOf(_loc3_) > -1 && this.mcChar.currentFrame > _loc4_ + 4)
            {
               _loc5_ = this.animQueue[0];
               this.mcChar.gotoAndPlay(_loc5_);
               if(_loc5_.indexOf("Attack") >= 0 && this.mcChar.weapon.mcWeapon.bAttack == true)
               {
                  this.mcChar.weapon.mcWeapon.gotoAndPlay("Attack");
               }
               this.animQueue.shift();
               return true;
            }
         }
         return false;
      }
      
      override public function gotoAndPlay(param1:Object, param2:String = null) : void
      {
         this.handleAnimEvent(String(param1));
         super.gotoAndPlay(param1);
      }
      
      private function hideOptionalParts() : void
      {
         var _loc1_:* = undefined;
         var _loc2_:* = ["cape","backhair","robe","backrobe"];
         for(_loc1_ in _loc2_)
         {
            if(typeof this.mcChar[_loc2_[_loc1_]] != undefined)
            {
               this.mcChar[_loc2_[_loc1_]].visible = false;
            }
         }
      }
      
      public function clearQueue() : void
      {
         this.animQueue = [];
      }
      
      public function walkTo(param1:int, param2:int, param3:int) : void
      {
         var dist:Number = NaN;
         var dx:Number = NaN;
         var _arg1:int = param1;
         var _arg2:int = param2;
         var _arg3:int = param3;
         var toX:int = _arg1;
         var toY:int = _arg2;
         var walkSpeed:int = _arg3;
         var isOK:Boolean = true;
         try
         {
            this.STAGE = MovieClip(parent);
         }
         catch(e:Error)
         {
            isOK = false;
         }
         if(isOK)
         {
            if(this.petLoaded == true)
            {
               this.mc.gotoAndPlay("Walk");
            }
            this.op = new Point(this.x,this.y);
            this.tp = new Point(toX,toY);
            this.walkSpeed = walkSpeed;
            dist = Number(Point.distance(this.op,this.tp));
            this.walkTS = new Date().getTime();
            this.walkD = Math.round(1000 * (dist / (walkSpeed * 22)));
            if(this.walkD > 0)
            {
               dx = this.op.x - this.tp.x;
               if(dx < 0)
               {
                  this.turn("left");
                  this.facingRight = true;
                  this.facingLeft = false;
               }
               else
               {
                  this.turn("right");
                  this.facingRight = false;
                  this.facingLeft = true;
               }
               if(!this.mcChar.onMove)
               {
                  this.mcChar.onMove = true;
                  if(this.mcChar.currentLabel != "Walk")
                  {
                     this.mcChar.gotoAndPlay("Walk");
                  }
               }
               this.removeEventListener(Event.ENTER_FRAME,this.onEnterFrameWalk);
               this.addEventListener(Event.ENTER_FRAME,this.onEnterFrameWalk);
            }
         }
      }
      
      private function linearTween(param1:*, param2:*, param3:*, param4:*) : Number
      {
         return param3 * param1 / param4 + param2;
      }
      
      private function onEnterFrameWalk(param1:Event) : void
      {
         var _loc2_:* = undefined;
         var _loc3_:* = undefined;
         var _loc4_:Boolean = false;
         var _loc5_:* = undefined;
         var _loc6_:* = undefined;
         var _loc7_:* = undefined;
         var _loc8_:* = undefined;
         var _loc9_:int = 0;
         var _loc10_:Boolean = false;
         var _loc11_:Point = null;
         var _loc12_:Rectangle = null;
         var _loc13_:Number = new Date().getTime();
         var _loc14_:Number = (_loc13_ - this.walkTS) / this.walkD;
         if(_loc14_ > 1)
         {
            _loc14_ = 1;
            this.stopWalking();
         }
         if(Point.distance(this.op,this.tp) > 0.5 && this.mcChar.onMove)
         {
            _loc2_ = this.x;
            _loc3_ = this.y;
            this.x = Point.interpolate(this.tp,this.op,_loc14_).x;
            this.y = Point.interpolate(this.tp,this.op,_loc14_).y;
            _loc4_ = false;
            _loc5_ = 0;
            if(this.STAGE.arrSolid == null)
            {
               this.STAGE.arrSolid = [];
            }
            while(_loc5_ < this.STAGE.arrSolid.length)
            {
               if(this.shadow.hitTestObject(this.STAGE.arrSolid[_loc5_].shadow))
               {
                  _loc4_ = true;
                  _loc5_ = this.STAGE.arrSolid.length;
               }
               _loc5_++;
            }
            if(_loc4_)
            {
               _loc6_ = this.y;
               this.y = _loc3_;
               _loc4_ = false;
               _loc7_ = 0;
               while(_loc7_ < this.STAGE.arrSolid.length)
               {
                  if(this.shadow.hitTestObject(this.STAGE.arrSolid[_loc7_].shadow))
                  {
                     this.y = _loc6_;
                     _loc4_ = true;
                     break;
                  }
                  _loc7_++;
               }
               if(_loc4_)
               {
                  this.x = _loc2_;
                  _loc4_ = false;
                  _loc8_ = 0;
                  while(_loc8_ < this.STAGE.arrSolid.length)
                  {
                     if(this.shadow.hitTestObject(this.STAGE.arrSolid[_loc8_].shadow))
                     {
                        _loc4_ = true;
                        break;
                     }
                     _loc8_++;
                  }
                  if(_loc4_)
                  {
                     this.x = _loc2_;
                     this.y = _loc3_;
                     this.stopWalking();
                  }
               }
            }
            if(Math.round(_loc2_) == Math.round(this.x) && Math.round(_loc3_) == Math.round(this.y) && _loc13_ > this.walkTS + 50)
            {
               this.stopWalking();
            }
         }
         else
         {
            this.stopWalking();
         }
      }
      
      public function stopWalking() : void
      {
         if(this.mcChar.onMove)
         {
            this.removeEventListener(Event.ENTER_FRAME,this.onEnterFrameWalk);
            if(Boolean(this.pAV.isMyAvatar) && Boolean(MovieClip(parent.parent).actionReady))
            {
               this.world.testAction(this.world.getAutoAttack());
            }
         }
         this.mcChar.onMove = false;
         if(this.walkSpeed > 23)
         {
            this.mcChar.gotoAndPlay("Fight");
         }
         else
         {
            this.mcChar.gotoAndPlay("Idle");
            if(this.petLoaded == true)
            {
               this.mc.gotoAndStop("Idle");
            }
         }
      }
      
      public function turn(param1:String) : void
      {
         if(param1 == "right" && this.mcChar.scaleX < 0 || param1 == "left" && this.mcChar.scaleX > 0)
         {
            this.mcChar.scaleX *= -1;
            if(this.petLoaded == true)
            {
               this.mc.scaleX *= -1;
            }
         }
      }
      
      public function endAction() : void
      {
         var _loc1_:Number = NaN;
         var _loc2_:String = null;
         var _loc3_:Object = null;
         var _loc4_:* = undefined;
         var _loc5_:* = null;
         if(this.pAV.target != null)
         {
            _loc5_ = this.pAV.target.pMC.mcChar;
         }
         if(!this.checkQueue(null))
         {
            if(this.mcChar.onMove)
            {
               this.mcChar.gotoAndPlay("Walk");
               _loc1_ = this.x - this.xTar;
               if(_loc1_ < 0)
               {
                  this.turn("right");
               }
               else
               {
                  this.turn("left");
               }
            }
            else if(_loc5_ == null || _loc5_ != null && (_loc5_.currentLabel == "Die" || _loc5_.currentLabel == "Feign" || _loc5_.currentLabel == "Dead" || this.pAV.target.npcType == "player" && (!("pvpTeam" in this.pAV.dataLeaf) || this.pAV.dataLeaf.pvpTeam == this.pAV.target.dataLeaf.pvpTeam)))
            {
               if(this.mcChar.currentLabel != "Jump")
               {
                  this.mcChar.gotoAndPlay("Idle");
               }
               if(_loc5_ != null)
               {
                  if(this.pAV.target.dataLeaf.intState == 0)
                  {
                     if(this.pAV == this.world.myAvatar)
                     {
                        this.world.setTarget(null);
                     }
                  }
               }
            }
            else
            {
               _loc2_ = "Fight";
               _loc3_ = this.pAV.getItemByEquipSlot("Weapon");
               if(_loc3_ != null && _loc3_.sType != null)
               {
                  _loc4_ = _loc3_.sType;
                  if(_loc3_.ItemID == 156)
                  {
                     _loc4_ = "Unarmed";
                  }
                  switch(_loc4_)
                  {
                     case "Unarmed":
                        _loc2_ = "UnarmedFight";
                        break;
                     case "Polearm":
                        _loc2_ = "PolearmFight";
                        break;
                     case "Dagger":
                        _loc2_ = "DuelWield/DaggerFight";
                  }
               }
               this.mcChar.gotoAndPlay(_loc2_);
            }
         }
      }
      
      public function addAnimationListener(param1:String, param2:Function) : void
      {
         if(this.animEvents[param1] == null)
         {
            this.animEvents[param1] = new Array();
         }
         if(!this.hasAnimationListener(param1,param2))
         {
            this.animEvents[param1].push(param2);
         }
      }
      
      public function removeAnimationListener(param1:String, param2:Function) : void
      {
         var _loc3_:uint = 0;
         if(this.animEvents[param1] == null)
         {
            return;
         }
         while(_loc3_ < this.animEvents[param1].length)
         {
            if(this.animEvents[param1][_loc3_] == param2)
            {
               this.animEvents[param1].splice(_loc3_,1);
               return;
            }
            _loc3_++;
         }
      }
      
      public function hasAnimationListener(param1:String, param2:Function) : Boolean
      {
         var _loc3_:uint = 0;
         if(this.animEvents[param1] == null)
         {
            return false;
         }
         while(_loc3_ < this.animEvents[param1].length)
         {
            if(this.animEvents[param1][_loc3_] == param2)
            {
               return true;
            }
            _loc3_++;
         }
         return false;
      }
      
      private function handleAnimEvent(param1:String) : void
      {
         var _loc2_:Function = null;
         var _loc3_:uint = 0;
         if(this.animEvents[param1] == null)
         {
            return;
         }
         while(_loc3_ < this.animEvents[param1].length)
         {
            _loc2_ = this.animEvents[param1][_loc3_];
            _loc2_();
            _loc3_++;
         }
      }
      
      public function get AnimEvent() : Object
      {
         return this.animEvents;
      }
      
      // Latest loader per slot. Loads are async, so a quick outfit swap can
      // finish an older load after a newer one was issued; completions from
      // anything but the latest loader are dropped (they would otherwise
      // read the newer load's domain/linkage and attach the wrong pieces).
      internal var slotLoaders:Object = {};

      private function startLoad(param1:String, param2:String, param3:Function, param4:ApplicationDomain) : void
      {
         var _loc5_:Loader = new Loader();
         this.slotLoaders[param1] = _loc5_.contentLoaderInfo;
         _loc5_.contentLoaderInfo.addEventListener(Event.COMPLETE,param3);
         _loc5_.contentLoaderInfo.addEventListener(IOErrorEvent.IO_ERROR,this.ioErrorHandler);
         _loc5_.loadBytes(Base64.decode(param2),new LoaderContext(false,param4));
      }

      private function isLatest(param1:String, param2:Event) : Boolean
      {
         return param2 == null || this.slotLoaders[param1] == param2.target;
      }

      public function loadArmor(param1:String, param2:String) : *
      {
         this.strSkinLinkage = param2;
         this.armorDomain = new ApplicationDomain();
         this.startLoad("armor",param1,this.onLoadSkinComplete,this.armorDomain);
      }

      // Emotes restart their loop counter (Stern and friends loop N times
      // off mcChar.animLoop, which otherwise carries over from the previous
      // emote), stop a click-walk in progress (its enter-frame tween would
      // yank the rig back to Walk/Idle) and ignore unknown labels instead of
      // throwing.
      public function loadEmote(param1:String) : *
      {
         if(!this.hasFrameLabel(param1))
         {
            return;
         }
         if(this.mcChar.onMove)
         {
            this.removeEventListener(Event.ENTER_FRAME,this.onEnterFrameWalk);
            this.mcChar.onMove = false;
         }
         this.mcChar.animLoop = 0;
         this.mcChar.gotoAndPlay(param1);
      }

      public function hasFrameLabel(param1:String) : Boolean
      {
         var _loc2_:Object = null;
         for each(_loc2_ in this.mcChar.currentLabels)
         {
            if(_loc2_.name == param1)
            {
               return true;
            }
         }
         return false;
      }

      // Scale keeps the facing direction (the sign of scaleX): walking left
      // flips the rig, and a resize used to snap it back to facing right.
      public function loadResize(param1:String) : *
      {
         var _loc2_:Number = Math.abs(Number(param1));
         if(isNaN(_loc2_) || _loc2_ == 0)
         {
            return;
         }
         this.mcChar.scaleX = this.mcChar.scaleX < 0 ? -_loc2_ : _loc2_;
         this.mcChar.scaleY = _loc2_;
         if(this.pAV.miscMC != null)
         {
            this.pAV.miscMC.scaleX = _loc2_;
            this.pAV.miscMC.scaleY = _loc2_;
         }
      }

      // "left" / "right" / "toggle": face the avatar (and pet) that way.
      public function setFacing(param1:String) : void
      {
         var _loc2_:String = String(param1).toLowerCase();
         if(_loc2_ == "toggle")
         {
            _loc2_ = this.mcChar.scaleX < 0 ? "right" : "left";
         }
         if(_loc2_ == "left" && this.mcChar.scaleX > 0 || _loc2_ == "right" && this.mcChar.scaleX < 0)
         {
            this.mcChar.scaleX *= -1;
            if(this.mc != null)
            {
               this.mc.scaleX *= -1;
            }
         }
      }
      
      public function loadResizePet(param1:String) : *
      {
         var _loc2_:Number = Math.abs(Number(param1));
         if(this.mc != null && !isNaN(_loc2_) && _loc2_ != 0)
         {
            // Keep the facing (sign of scaleX), like loadResize does.
            this.mc.scaleX = this.mc.scaleX < 0 ? -_loc2_ : _loc2_;
            this.mc.scaleY = _loc2_;
         }
      }
      
      // Armor pieces, per the live game's loadArmorPiecesFromDomain: every
      // piece is independent (one missing symbol no longer aborts the rest
      // and leaves the previous armor's pieces mixed in), the Foot symbol
      // goes to BOTH feet (the front foot is the one shown while walking),
      // and pieces the new armor lacks (Robe / RobeBack) are hidden instead
      // of keeping the previous armor's art on screen.
      private function onLoadSkinComplete(param1:Event) : *
      {
         if(!this.isLatest("armor",param1))
         {
            return;
         }
         var AssetClass:Class = null;
         this.strGender = this.pAV.objData.strGender;
         var g:String = this.strGender;
         // Gender mismatch (file only ships the other gender's symbols, e.g.
         // a dropped SWF): render it rather than leaving the template body.
         if(!this.armorDomain.hasDefinition(this.strSkinLinkage + g + "Chest"))
         {
            var alt:String = g == "M" ? "F" : "M";
            if(this.armorDomain.hasDefinition(this.strSkinLinkage + alt + "Chest"))
            {
               g = alt;
            }
         }
         var base:String = this.strSkinLinkage + g;
         AssetClass = this.armorClass(base + "Head");
         if(AssetClass == null)
         {
            try
            {
               AssetClass = getDefinitionByName("mcHead" + g) as Class;
            }
            catch(err:Error)
            {
               AssetClass = null;
            }
         }
         if(AssetClass != null)
         {
            if(this.mcChar.head.numChildren > 0)
            {
               this.mcChar.head.removeChildAt(0);
            }
            this.mcChar.head.addChildAt(new AssetClass(),0);
         }
         this.setPiece(base + "Chest",[this.mcChar.chest]);
         this.setPiece(base + "Hip",[this.mcChar.hip]);
         this.setPiece(base + "FootIdle",[this.mcChar.idlefoot]);
         this.setPiece(base + "Foot",[this.mcChar.frontfoot,this.mcChar.backfoot]);
         this.setPiece(base + "Shoulder",[this.mcChar.frontshoulder,this.mcChar.backshoulder]);
         if(this.setPiece(base + "Hand",[this.mcChar.fronthand,this.mcChar.backhand]))
         {
            // The skeleton draws the far limbs as black silhouettes via its
            // timeline, except the back hand; the game blacks it out in code
            // (fl.motion.Color brightness -1 == all multipliers 0).
            this.mcChar.backhand.getChildAt(0).transform.colorTransform = new ColorTransform(0,0,0,1,0,0,0,0);
         }
         this.setPiece(base + "Thigh",[this.mcChar.frontthigh,this.mcChar.backthigh]);
         this.setPiece(base + "Shin",[this.mcChar.frontshin,this.mcChar.backshin]);
         this.armorHasRobe = this.setPiece(base + "Robe",[this.mcChar.robe]);
         this.armorHasRobeBack = this.setPiece(base + "RobeBack",[this.mcChar.backrobe]);
         this.mcChar.robe.visible = this.armorHasRobe && !this.armorHidden;
         this.mcChar.backrobe.visible = this.armorHasRobeBack && !this.armorHidden;
         visible = true;
      }

      private function armorClass(param1:String) : Class
      {
         try
         {
            if(this.armorDomain != null && this.armorDomain.hasDefinition(param1))
            {
               return this.armorDomain.getDefinition(param1) as Class;
            }
         }
         catch(err:Error)
         {
         }
         return null;
      }

      // Swap one armor symbol into each given rig slot. Returns false (slot
      // untouched) when the armor has no such symbol.
      private function setPiece(param1:String, param2:Array) : Boolean
      {
         var _loc3_:Class = this.armorClass(param1);
         var _loc4_:MovieClip = null;
         if(_loc3_ == null)
         {
            return false;
         }
         for each(_loc4_ in param2)
         {
            if(_loc4_.numChildren > 0)
            {
               _loc4_.removeChildAt(0);
            }
            _loc4_.addChildAt(new _loc3_(),0);
         }
         return true;
      }
      
      private function ioErrorHandler(param1:IOErrorEvent) : void
      {
      }

      // ---- head: hair / helm / back hair -------------------------------
      // One place decides what the head shows (the live game's
      // setHelmVisibility + onHairLoadComplete rules): a worn, shown helm
      // hides the hair and shows the helm's own "<link>_backhair" (or no
      // back hair); otherwise the hair and the hair's own HairBack show.
      // Load order no longer matters and hiding the helm never leaves a
      // bald head.
      internal var hairClass:Class;

      internal var hairBackClass:Class;

      internal var helmBackClass:Class;

      internal var backhairShown:Class;

      public var helmLoaded:Boolean = false;

      public var helmShown:Boolean = true;

      public var hairHidden:Boolean = false;

      public var armorHidden:Boolean = false;

      internal var armorHasRobe:Boolean = false;

      internal var armorHasRobeBack:Boolean = false;

      public var capeShown:Boolean = true;
      
      public var capeLoaded:Boolean = false;

      public var petShown:Boolean = true;

      public var miscShown:Boolean = true;

      public static function isTrue(param1:*) : Boolean
      {
         return String(param1).toLowerCase() == "true";
      }

      public function refreshHead() : void
      {
         var _loc1_:Boolean = this.helmLoaded && this.helmShown;
         var _loc2_:Class = null;
         this.mcChar.head.helm.visible = this.helmShown;
         this.mcChar.head.hair.visible = !this.hairHidden && !_loc1_;
         if(_loc1_)
         {
            _loc2_ = this.helmBackClass;
         }
         else if(!this.hairHidden)
         {
            _loc2_ = this.hairBackClass;
         }
         if(_loc2_ == null)
         {
            this.mcChar.backhair.visible = false;
            return;
         }
         if(this.backhairShown != _loc2_)
         {
            while(this.mcChar.backhair.numChildren > 0)
            {
               this.mcChar.backhair.removeChildAt(0);
            }
            this.mcChar.backhair.addChild(new _loc2_());
            this.backhairShown = _loc2_;
         }
         this.mcChar.backhair.visible = true;
      }

      public function setHelmVisibility(param1:Boolean) : void
      {
         this.helmShown = param1;
         this.refreshHead();
      }

      private static function domainClass(param1:ApplicationDomain, param2:String) : Class
      {
         try
         {
            if(param1 != null && param1.hasDefinition(param2))
            {
               return param1.getDefinition(param2) as Class;
            }
         }
         catch(err:Error)
         {
         }
         return null;
      }

      public function loadHair(param1:String, param2:String) : void
      {
         this.pAV.objData.strHairName = param2;
         this.hairDomain = new ApplicationDomain();
         this.startLoad("hair",param1,this.onHairLoadComplete,this.hairDomain);
      }

      // HairBack lives in the hair file (it was looked up in the armor's
      // domain, so long hair never got its back half). A hair shipped for
      // the other gender only still renders instead of leaving a bald head.
      private function onHairLoadComplete(param1:Event) : void
      {
         if(!this.isLatest("hair",param1))
         {
            return;
         }
         var _loc2_:String = this.pAV.objData.strHairName;
         var _loc3_:String = this.pAV.objData.strGender;
         var _loc4_:Class = domainClass(this.hairDomain,_loc2_ + _loc3_ + "Hair");
         if(_loc4_ == null)
         {
            _loc3_ = _loc3_ == "M" ? "F" : "M";
            _loc4_ = domainClass(this.hairDomain,_loc2_ + _loc3_ + "Hair");
         }
         if(_loc4_ == null)
         {
            return;
         }
         while(this.mcChar.head.hair.numChildren > 0)
         {
            this.mcChar.head.hair.removeChildAt(0);
         }
         this.mcChar.head.hair.addChild(new _loc4_());
         this.hairClass = _loc4_;
         this.hairBackClass = domainClass(this.hairDomain,_loc2_ + _loc3_ + "HairBack");
         this.refreshHead();
      }

      public function setColor(param1:MovieClip, param2:String, param3:String) : void
      {
         var _loc4_:Number = Number(this.pAV.objData["intColor" + param2]);
         param1.isColored = true;
         param1.intColor = _loc4_;
         param1.strLocation = param2;
         param1.strShade = param3;
         this.changeColor(param1,_loc4_,param3);
      }

      // Shade offsets per the live game: DARK pulls red down by 50 like the
      // other channels, except on Skin (25) - using 25 everywhere left every
      // dark Base/Trim/Accessory/Hair/Eye shade too red.
      public function changeColor(param1:MovieClip, param2:Number, param3:String) : void
      {
         var _loc4_:ColorTransform = new ColorTransform();
         _loc4_.color = param2;
         switch(String(param3).toUpperCase())
         {
            case "LIGHT":
               _loc4_.redOffset += 100;
               _loc4_.greenOffset += 100;
               _loc4_.blueOffset += 100;
               break;
            case "DARK":
               _loc4_.redOffset -= param1.strLocation == "Skin" ? 25 : 50;
               _loc4_.greenOffset -= 50;
               _loc4_.blueOffset -= 50;
               break;
            case "DARKER":
               _loc4_.redOffset -= 125;
               _loc4_.greenOffset -= 125;
               _loc4_.blueOffset -= 125;
         }
         param1.transform.colorTransform = _loc4_;
      }

      public function updateColor(param1:Object = null) : *
      {
         this.scanColor(this,param1 != null ? param1 : this.pAV.objData);
      }

      private function scanColor(param1:MovieClip, param2:*) : void
      {
         var _loc3_:DisplayObject = null;
         var _loc4_:int = 0;
         if("isColored" in param1)
         {
            param1.intColor = Number(param2["intColor" + param1.strLocation]);
            this.changeColor(param1,param1.intColor,param1.strShade);
         }
         while(_loc4_ < param1.numChildren)
         {
            _loc3_ = param1.getChildAt(_loc4_);
            if(_loc3_ is MovieClip)
            {
               this.scanColor(MovieClip(_loc3_),param2);
            }
            _loc4_++;
         }
      }

      private function addGlow(param1:MovieClip) : void
      {
         if(this.noGlow)
         {
            return;
         }
         var _loc2_:* = new GlowFilter(16777215,1,8,8,2,1,false,false);
         param1.filters = [_loc2_];
      }

      public function loadWeapon(param1:String, param2:String, param3:String = null) : void
      {
         this.pAV.objData.strWeaponLink = param2;
         this.pAV.objData.strWeaponType = param3;
         this.weaponDomain = new ApplicationDomain();
         this.startLoad("weapon",param1,this.onLoadWeaponComplete,this.weaponDomain);
      }

      // Gauntlets are worn, not held: the game puts one copy of the art in
      // each hand clip (above the hand at index 1), at 80% scale, mirrored,
      // and leaves the weapon slot empty. The art picks its fore/back-hand
      // look itself by checking whether it sits in "fronthand" or
      // "backhand". Dagger (dual wield) is the only other special case.
      public var weaponShown:Boolean = true;

      // Off-hand copy wanted (Dagger set, or dagger mode on). Kept apart from
      // weaponOff.visible so hiding the weapon can hide the off hand too and
      // showing it again brings it back.
      public var dualShown:Boolean = false;

      public var gauntletMode:Boolean = false;

      private function clearGauntlet() : void
      {
         var _loc1_:MovieClip = null;
         for each(_loc1_ in [this.mcChar.fronthand,this.mcChar.backhand])
         {
            while(_loc1_.numChildren > 1)
            {
               _loc1_.removeChildAt(1);
            }
         }
      }

      private function addGauntlet(param1:MovieClip, param2:Class) : void
      {
         var _loc3_:MovieClip = new param2() as MovieClip;
         _loc3_.scaleX = -0.8;
         _loc3_.scaleY = 0.8;
         _loc3_.visible = this.weaponShown;
         param1.addChildAt(_loc3_,1);
      }

      // Hide weapon (unarmed): the held weapon, a dual set's off-hand copy
      // (it used to stay in the hand), or the gauntlet pieces.
      public function setWeaponShown(param1:Boolean) : void
      {
         var _loc2_:MovieClip = null;
         this.weaponShown = param1;
         this.mcChar.weapon.visible = param1 && !this.gauntletMode;
         this.mcChar.weaponOff.visible = param1 && this.dualShown && !this.gauntletMode;
         for each(_loc2_ in [this.mcChar.fronthand,this.mcChar.backhand])
         {
            if(_loc2_.numChildren > 1)
            {
               _loc2_.getChildAt(1).visible = param1;
            }
         }
      }

      public function onLoadWeaponComplete(param1:Event) : void
      {
         var x:* = undefined;
         var xx:* = undefined;
         var e:Event = param1;
         var AssetClass:* = undefined;
         if(!this.isLatest("weapon",e))
         {
            return;
         }
         this.clearGauntlet();
         this.gauntletMode = this.pAV.objData.strWeaponType == "Gauntlet";
         if(this.gauntletMode)
         {
            AssetClass = domainClass(this.weaponDomain,this.pAV.objData.strWeaponLink);
            if(AssetClass != null)
            {
               while(this.mcChar.weapon.hi.numChildren > 0)
               {
                  this.mcChar.weapon.hi.removeChildAt(0);
               }
               while(this.mcChar.weapon.numChildren > 1)
               {
                  this.mcChar.weapon.removeChildAt(1);
               }
               this.addGauntlet(this.mcChar.fronthand,AssetClass);
               this.addGauntlet(this.mcChar.backhand,AssetClass);
               this.mcChar.weaponOff.visible = false;
               this.setWeaponShown(this.weaponShown);
               return;
            }
            this.gauntletMode = false;
         }
         this.mcChar.weapon.visible = this.weaponShown;
         while(this.mcChar.weapon.hi.numChildren > 0)
         {
            this.mcChar.weapon.hi.removeChildAt(0);
         }
         while(this.mcChar.weaponOff.hi.numChildren > 0)
         {
            this.mcChar.weaponOff.hi.removeChildAt(0);
         }
         try
         {
            while(this.mcChar.weapon.numChildren > 1)
            {
               this.mcChar.weapon.removeChildAt(1);
            }
            while(this.mcChar.weaponOff.numChildren > 1)
            {
               this.mcChar.weaponOff.removeChildAt(1);
            }
            AssetClass = this.weaponDomain.getDefinition(this.pAV.objData.strWeaponLink) as Class;
            x = new AssetClass();
            xx = new AssetClass();
            if(this.pAV.objData.strWeaponType == "Dagger")
            {
               this.mcChar.weapon.addChild(x);
               this.mcChar.weaponOff.addChild(xx);
               this.dualShown = true;
               this.mcChar.weaponOff.visible = this.weaponShown;
            }
            else
            {
               this.mcChar.weaponOff.hi.addChild(xx);
               this.mcChar.weapon.hi.addChild(x);
            }
         }
         catch(err:Error)
         {
            this.mcChar.weapon.hi.addChild(e.target.content);
         }
      }

      public function loadWeaponOff() : void
      {
         var _loc1_:* = new Loader();
         _loc1_.contentLoaderInfo.addEventListener(Event.COMPLETE,this.onLoadWeaponOffComplete);
         _loc1_.load(new URLRequest(this.serverFilePath + this.pAV.objData.strWeaponFile),new LoaderContext(false,this.weaponDomain));
      }

      public function onLoadWeaponOffComplete(param1:Event) : void
      {
         var e:Event = param1;
         var AssetClass:* = undefined;
         this.mcChar.weaponOff.removeChildAt(0);
         try
         {
            AssetClass = this.weaponDomain.getDefinition(this.pAV.objData.strWeaponLink) as Class;
            this.mcChar.weaponOff.addChild(new AssetClass());
         }
         catch(err:Error)
         {
            mcChar.weaponOff.addChild(e.target.content);
         }
         this.dualShown = true;
         this.mcChar.weaponOff.visible = this.weaponShown;
      }

      public function loadCape(param1:String, param2:String) : void
      {
         this.pAV.objData.strCapeLink = param2;
         this.capeDomain = new ApplicationDomain();
         this.startLoad("cape",param1,this.onLoadCapeComplete,this.capeDomain);
      }

      // A pending "Hide cape" survives the load (it used to be re-shown by
      // the load finishing after the hide call).
      public function onLoadCapeComplete(param1:Event) : void
      {
         if(!this.isLatest("cape",param1))
         {
            return;
         }
         var _loc2_:Class = domainClass(this.capeDomain,this.pAV.objData.strCapeLink);
         if(_loc2_ == null)
         {
            return;
         }
         while(this.mcChar.cape.numChildren > 0)
         {
            this.mcChar.cape.removeChildAt(0);
         }
         this.mcChar.cape.cape = new _loc2_();
         this.mcChar.cape.addChild(this.mcChar.cape.cape);
         this.capeLoaded = true;
         this.mcChar.cape.visible = this.capeShown;
      }

      public function loadHelm(param1:String, param2:String) : void
      {
         this.pAV.objData.strHelmLink = param2;
         this.helmDomain = new ApplicationDomain();
         this.startLoad("helm",param1,this.onLoadHelmComplete,this.helmDomain);
      }

      public function onLoadHelmComplete(param1:Event) : void
      {
         if(!this.isLatest("helm",param1))
         {
            return;
         }
         var _loc2_:Class = domainClass(this.helmDomain,this.pAV.objData.strHelmLink);
         if(_loc2_ == null)
         {
            return;
         }
         while(this.mcChar.head.helm.mhead.numChildren > 0)
         {
            this.mcChar.head.helm.mhead.removeChildAt(0);
         }
         this.mcChar.head.helm.mhead.addChild(new _loc2_());
         this.mcChar.head.helm.mhead.visible = true;
         this.helmBackClass = domainClass(this.helmDomain,this.pAV.objData.strHelmLink + "_backhair");
         this.helmLoaded = true;
         this.refreshHead();
      }

      public function loadPet(param1:String, param2:String) : void
      {
         this.pAV.objData.strPetLink = param2;
         this.petDomain = new ApplicationDomain();
         this.startLoad("pet",param1,this.onLoadPetComplete,this.petDomain);
      }

      public function petAnimations() : void
      {
         this.mc.gotoAndPlay("Walk");
      }

      public function onLoadPetComplete(param1:Event, param2:String = null) : void
      {
         if(!this.isLatest("pet",param1))
         {
            return;
         }
         var _loc3_:Class = domainClass(this.petDomain,this.pAV.objData.strPetLink);
         if(_loc3_ == null)
         {
            return;
         }
         var _loc4_:Number = 1;
         if(this.mc != null)
         {
            _loc4_ = Math.abs(this.mc.scaleY);
            if(this.mc.parent == this)
            {
               removeChild(this.mc);
            }
         }
         this.AssetClassPet = _loc3_;
         this.mc = new this.AssetClassPet();
         this.mc.x = -40;
         this.mc.y = 10;
         this.mc.scaleX = this.mcChar.scaleX < 0 ? -_loc4_ : _loc4_;
         this.mc.scaleY = _loc4_;
         this.mc.visible = this.petShown;
         addChild(this.mc);
         this.petLoaded = true;
         ExternalInterface.call("setPetSize");
      }

      public function loadMisc(param1:String, param2:String) : void
      {
         this.pAV.objData.strMiscLink = param2;
         this.miscDomain = new ApplicationDomain();
         this.startLoad("misc",param1,this.onLoadMiscComplete,this.miscDomain);
      }

      public function onLoadMiscComplete(param1:Event) : void
      {
         if(!this.isLatest("misc",param1))
         {
            return;
         }
         var _loc2_:Class = domainClass(this.miscDomain,this.pAV.objData.strMiscLink);
         var _loc3_:MovieClip = null;
         var _loc4_:MovieClip = this.shadow;
         if(_loc2_ == null)
         {
            return;
         }
         if(this.pAV.miscMC == null)
         {
            _loc3_ = new MovieClip();
            _loc3_.mouseChildren = false;
            _loc3_.mouseEnabled = false;
            this.addChildAt(_loc3_,this.getChildIndex(_loc4_));
            _loc3_.x = _loc4_.x;
            _loc3_.y = _loc4_.y;
            this.pAV.miscMC = _loc3_;
         }
         _loc3_ = this.pAV.miscMC;
         while(_loc3_.numChildren > 0)
         {
            _loc3_.removeChildAt(0);
         }
         _loc3_.addChild(new _loc2_());
         _loc3_.visible = this.miscShown;
         _loc3_.scaleX = Math.abs(this.mcChar.scaleX);
         _loc3_.scaleY = this.mcChar.scaleY;
         this.shadow.alpha = this.miscShown ? 0 : 1;
      }

      public function hideMisc(param1:String) : void
      {
         this.miscShown = !isTrue(param1);
         if(this.pAV.miscMC != null)
         {
            this.pAV.miscMC.visible = this.miscShown;
            this.shadow.alpha = this.miscShown ? 0 : 1;
         }
      }

      public function hideHair(param1:String) : void
      {
         this.hairHidden = isTrue(param1);
         this.refreshHead();
      }

      // Hides/shows the armor-driven body pieces. Feet follow the rig's
      // current animation (mcSkel.frontFootShown) so a walk keeps the right
      // foot, and robe pieces only return if the current armor has them.
      public function hideArmor(param1:String) : void
      {
         this.armorHidden = isTrue(param1);
         var _loc2_:Boolean = !this.armorHidden;
         var _loc3_:Boolean = Boolean(this.mcChar["frontFootShown"]);
         this.mcChar.hideFeet = this.armorHidden;
         this.mcChar.chest.visible = _loc2_;
         this.mcChar.hip.visible = _loc2_;
         this.mcChar.idlefoot.visible = _loc2_ && !_loc3_;
         this.mcChar.frontfoot.visible = _loc2_ && _loc3_;
         this.mcChar.backfoot.visible = _loc2_;
         this.mcChar.frontshoulder.visible = _loc2_;
         this.mcChar.backshoulder.visible = _loc2_;
         this.mcChar.fronthand.visible = _loc2_;
         this.mcChar.backhand.visible = _loc2_;
         this.mcChar.frontthigh.visible = _loc2_;
         this.mcChar.backthigh.visible = _loc2_;
         this.mcChar.frontshin.visible = _loc2_;
         this.mcChar.backshin.visible = _loc2_;
         this.mcChar.robe.visible = _loc2_ && this.armorHasRobe;
         this.mcChar.backrobe.visible = _loc2_ && this.armorHasRobeBack;
      }
      // Diagnostics for Manikin --headless: a JSON snapshot of the rig
      // (per-part children, visibility, dye transforms, animation label).
      public function getAvatarState() : String
      {
         var c:MovieClip = this.mcChar;
         var o:Object = {};
         o.label = c.currentLabel;
         o.frame = c.currentFrame;
         o.scaleX = c.scaleX;
         o.scaleY = c.scaleY;
         o.headOk = c.head != null && c.head.parent == c;
         o.gender = this.pAV.objData != null ? this.pAV.objData.strGender : null;
         o.gauntlet = this.gauntletMode;
         o.weaponShown = this.weaponShown;
         var parts:Array = ["head","chest","hip","idlefoot","frontfoot","backfoot","frontshoulder","backshoulder","fronthand","backhand","frontthigh","backthigh","frontshin","backshin","robe","backrobe","backhair","cape","weapon","weaponOff","shield"];
         var p:Object = {};
         var n:String = null;
         for each(n in parts)
         {
            p[n] = this.describeClip(c[n] as DisplayObjectContainer);
         }
         o.parts = p;
         if(c.head != null)
         {
            o.hair = this.describeClip(c.head.hair as DisplayObjectContainer);
            o.helm = this.describeClip(c.head.helm as DisplayObjectContainer);
            o.helmHead = c.head.helm != null ? this.describeClip(c.head.helm.mhead as DisplayObjectContainer) : null;
         }
         var cols:Array = [];
         var seen:Object = {};
         this.collectColored(this,cols,seen);
         o.colored = cols;
         if(this.pAV.objData != null)
         {
            o.colors = {
               "Hair":this.pAV.objData.intColorHair,
               "Skin":this.pAV.objData.intColorSkin,
               "Eye":this.pAV.objData.intColorEye,
               "Trim":this.pAV.objData.intColorTrim,
               "Base":this.pAV.objData.intColorBase,
               "Accessory":this.pAV.objData.intColorAccessory
            };
         }
         o.pet = this.mc != null ? {
            "cls":getQualifiedClassName(this.mc),
            "v":this.mc.visible,
            "sx":this.mc.scaleX
         } : null;
         o.misc = this.pAV.miscMC != null ? this.describeClip(this.pAV.miscMC as DisplayObjectContainer) : null;
         var root:* = this.parent;
         if(root != null && "txtWeapon" in root)
         {
            o.names = {
               "user":root.txtName.text,
               "weapon":root.txtWeapon.text,
               "armor":root.txtArmor.text,
               "helm":root.txtHelm.text,
               "cape":root.txtCape.text,
               "pet":root.txtPet.text
            };
            var nb:Rectangle = root.txtName.getBounds(root);
            var hb:Rectangle = this.mcChar.head.getBounds(root);
            var ab:Rectangle = this.mcChar.getBounds(root);
            o.nameTag = {
               "v":root.txtName.visible,
               "cx":nb.x + nb.width / 2,
               "bottom":nb.bottom,
               "headTop":hb.top,
               "bodyLeft":ab.left,
               "bodyRight":ab.right
            };
         }
         return JSON.stringify(o);
      }

      private function describeClip(param1:DisplayObjectContainer) : Object
      {
         if(param1 == null)
         {
            return null;
         }
         var _loc2_:Array = [];
         var _loc3_:int = 0;
         var _loc4_:DisplayObject = null;
         while(_loc3_ < param1.numChildren)
         {
            _loc4_ = param1.getChildAt(_loc3_);
            _loc2_.push(getQualifiedClassName(_loc4_) + (_loc4_.visible ? "" : "(hidden)"));
            _loc3_++;
         }
         return {
            "v":param1.visible,
            "attached":param1.stage != null,
            "kids":_loc2_,
            "m0":(param1.numChildren > 0 ? param1.getChildAt(0).transform.colorTransform.redMultiplier : -1),
            "depth":(param1.parent != null ? param1.parent.getChildIndex(param1) : -1)
         };
      }

      private function collectColored(param1:DisplayObjectContainer, param2:Array, param3:Object) : void
      {
         var _loc4_:ColorTransform = null;
         var _loc5_:String = null;
         var _loc6_:int = 0;
         var _loc7_:DisplayObjectContainer = null;
         if(param1 is MovieClip && "isColored" in param1)
         {
            _loc4_ = param1.transform.colorTransform;
            _loc5_ = param1["strLocation"] + "|" + param1["strShade"] + "|" + _loc4_.redOffset + "," + _loc4_.greenOffset + "," + _loc4_.blueOffset + "|" + _loc4_.redMultiplier;
            if(param3[_loc5_] == null)
            {
               param3[_loc5_] = true;
               param2.push({
                  "loc":param1["strLocation"],
                  "shade":param1["strShade"],
                  "r":_loc4_.redOffset,
                  "g":_loc4_.greenOffset,
                  "b":_loc4_.blueOffset,
                  "m":_loc4_.redMultiplier
               });
            }
         }
         while(_loc6_ < param1.numChildren)
         {
            _loc7_ = param1.getChildAt(_loc6_) as DisplayObjectContainer;
            if(_loc7_ != null)
            {
               this.collectColored(_loc7_,param2,param3);
            }
            _loc6_++;
         }
      }

      public function getMiscInfo() : int
      {
         var _loc2_:* = 0;
         var _loc3_:* = this.pAV.miscMC;
         if(_loc3_ != null)
         {
            _loc2_ += 1;
            if(_loc3_.numChildren > 0)
            {
               _loc2_ += 2;
            }
            if(_loc3_.visible != false)
            {
               _loc2_ += 4;
            }
            return _loc2_;
         }
         return _loc2_;
      }
   }
}

