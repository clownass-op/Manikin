package
{
   import flash.display.GradientType;
   import flash.display.Sprite;
   import flash.geom.Matrix;

   // Background scene: the dark navy glow from the catalog picker mockup
   // (CSS radial-gradient(ellipse at 50% 100%, #2a2f4a 0%, #141726 45%,
   // #0A0C14 80%)). Same 1280x720 stage as the player and the newer scenes;
   // the fill runs far past the stage so a scaled-down or dragged scene
   // never shows an edge. Build (Flex SDK 4.16.1, from the project root):
   //   mxmlc -target-player=32.0 -swf-version=43 -static-link-runtime-shared-libraries
   //         -output flash/Gradient.swf tools/bg/Gradient.as
   [SWF(width="1280", height="720", frameRate="24", backgroundColor="#0A0C14")]
   public class Gradient extends Sprite
   {
      public function Gradient()
      {
         var m:Matrix = new Matrix();
         // Ellipse centred on the bottom edge, wider than tall like the CSS one.
         m.createGradientBox(1400, 1100, 0, 640 - 700, 720 - 550);
         graphics.beginGradientFill(GradientType.RADIAL,
            [0x2A2F4A, 0x141726, 0x0A0C14], [1, 1, 1], [0, 115, 204], m);
         graphics.drawRect(-4000, -4000, 9280, 8720);
         graphics.endFill();
      }
   }
}
