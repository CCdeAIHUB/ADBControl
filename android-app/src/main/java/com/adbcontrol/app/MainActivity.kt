package com.adbcontrol.app

import android.app.Activity
import android.content.Intent
import android.media.projection.MediaProjectionManager
import android.os.Bundle
import androidx.activity.ComponentActivity
import com.adbcontrol.app.service.ScreenCaptureService

class MainActivity : ComponentActivity() {
    private lateinit var projectionManager: MediaProjectionManager

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        projectionManager = getSystemService(MediaProjectionManager::class.java)
        startActivityForResult(projectionManager.createScreenCaptureIntent(), REQUEST_SCREEN_CAPTURE)
    }

    @Deprecated("Uses Activity result callback for minimal bootstrap compatibility.")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == REQUEST_SCREEN_CAPTURE && resultCode == Activity.RESULT_OK && data != null) {
            val intent = Intent(this, ScreenCaptureService::class.java).apply {
                putExtra(ScreenCaptureService.EXTRA_RESULT_CODE, resultCode)
                putExtra(ScreenCaptureService.EXTRA_RESULT_DATA, data)
            }
            startForegroundService(intent)
        }
    }

    private companion object {
        const val REQUEST_SCREEN_CAPTURE = 1001
    }
}
